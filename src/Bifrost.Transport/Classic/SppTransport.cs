using Android.Bluetooth;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Core.Threading;
using Java.Util;

namespace Bifrost.Transport.Classic;

/// <summary>
/// Bluetooth Classic over RFCOMM (Serial Port Profile) — what the majority of mobile thermal
/// printers expose, and what the Web Bluetooth API cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// A convenience of .NET for Android: <c>BluetoothSocket.InputStream</c> and
/// <c>OutputStream</c> surface as ordinary <see cref="Stream"/> objects, so this is plain C# stream
/// I/O with real <see cref="CancellationToken"/> support — no interop shim (ADR-008).
/// </para>
/// <para>See Docs/03-design/06-printer-abstraction.md §6.</para>
/// </remarks>
public sealed class SppTransport : IPrinterTransport
{
    /// <summary>The well-known Serial Port Profile UUID.</summary>
    private static readonly UUID SppUuid =
        UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    private readonly StateStream<ConnectionState> _state = new(new ConnectionState.Disconnected());
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private BluetoothSocket? _socket;
    private Stream? _output;
    private Stream? _input;

    public TransportType Type => TransportType.BtClassic;

    public IStateStream<ConnectionState> ConnectionState => _state;

    public async Task<Result> ConnectAsync(string address, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _state.Publish(new ConnectionState.Connecting());

        try
        {
            // Not disposed: the adapter is a process-wide singleton.
            var adapter = BluetoothAccess.Adapter;
            if (adapter is null || !adapter.IsEnabled)
            {
                return Fail(new PrinterError.ConnectionFailed("Bluetooth is off or unavailable."));
            }

            // An active discovery cripples RFCOMM throughput and makes connects fail
            // intermittently. Always cancel it first — DES-06 §6.
            if (adapter.IsDiscovering) adapter.CancelDiscovery();

            var device = adapter.GetRemoteDevice(address);
            if (device is null)
            {
                return Fail(new PrinterError.ConnectionFailed($"No paired device at {address}."));
            }

            _socket = device.CreateRfcommSocketToServiceRecord(SppUuid);
            if (_socket is null)
            {
                return Fail(new PrinterError.ConnectionFailed("Could not open an RFCOMM socket."));
            }

            await _socket.ConnectAsync().ConfigureAwait(false);

            _output = _socket.OutputStream;
            _input = _socket.InputStream;

            if (_output is null)
            {
                return Fail(new PrinterError.ConnectionFailed("Socket opened but has no output stream."));
            }

            _state.Publish(new ConnectionState.Connected(device.Name ?? address, Mtu: null));
            return Result.Ok();
        }
        catch (Java.IO.IOException ex)
        {
            return Fail(new PrinterError.ConnectionFailed(ex.Message ?? "RFCOMM connect failed."));
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Missing BLUETOOTH_CONNECT on API 31+ lands here.
            return Fail(new PrinterError.ConnectionFailed(
                $"Bluetooth permission denied: {ex.Message}"));
        }
    }

    public async Task<Result> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var output = _output;
        if (output is null || _socket?.IsConnected != true)
        {
            return Result.Fail(new PrinterError.Disconnected());
        }

        // One writer at a time. The single-consumer model upstream should already guarantee this,
        // but two half-labels printed on top of each other is not a failure worth risking on an
        // assumption (ADR-005).
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Chunked at 4 KB. RFCOMM handles flow control itself, so this is not a protocol
            // requirement — it is so that a stalled printer is detected promptly rather than after
            // the whole payload has been handed to a blocking write.
            const int chunkSize = 4096;

            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var chunk = bytes.Slice(offset, Math.Min(chunkSize, bytes.Length - offset));
                await output.WriteAsync(chunk, ct).ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            // The per-job transmit timeout (FR-609) arrives here. Transient: worth a retry.
            return Result.Fail(new PrinterError.TransmitTimeout());
        }
        catch (Java.IO.IOException ex)
        {
            _state.Publish(new ConnectionState.Failed(new PrinterError.Disconnected()));
            return Result.Fail(new PrinterError.ConnectionFailed(ex.Message ?? "Write failed."));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Result<byte[]>> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var input = _input;
        if (input is null) return Result<byte[]>.Fail(new PrinterError.Disconnected());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var buffer = new byte[256];
            var read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            return Result<byte[]>.Ok(buffer[..read]);
        }
        catch (OperationCanceledException)
        {
            // Silence is the normal answer from a write-only printer. An empty response is a fact,
            // not a failure — the driver decides what it means (FR-608).
            return Result<byte[]>.Ok([]);
        }
        catch (Java.IO.IOException ex)
        {
            return Result<byte[]>.Fail(new PrinterError.ConnectionFailed(ex.Message ?? "Read failed."));
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            _output?.Dispose();
            _input?.Dispose();
            _socket?.Close();
        }
        catch (Java.IO.IOException)
        {
            // Already gone. Nothing useful to do or report.
        }
        finally
        {
            _output = null;
            _input = null;
            _socket?.Dispose();
            _socket = null;
            _state.Publish(new ConnectionState.Disconnected());
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private Result Fail(PrinterError error)
    {
        _state.Publish(new ConnectionState.Failed(error));
        return Result.Fail(error);
    }
}
