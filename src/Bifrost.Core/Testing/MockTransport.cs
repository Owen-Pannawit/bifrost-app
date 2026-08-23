using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Core.Threading;

namespace Bifrost.Core.Testing;

/// <summary>
/// An <see cref="IPrinterTransport"/> that records bytes instead of sending them, and can be made
/// to fail in the specific ways real printers fail.
/// </summary>
/// <remarks>
/// <para>
/// The single most important piece of test infrastructure in the project: it is what allows the
/// entire print path to be built and verified <b>before a printer is purchased</b> (Q-01, D-07,
/// NFR-602).
/// </para>
/// <para>
/// <b>Deviation from IMP-02 §3.3.</b> The documentation places this in <c>Bifrost.Transport</c>.
/// That project targets <c>net10.0-android</c>, so a platform-free test project cannot reference
/// it — which would defeat the requirement this class exists to satisfy. It lives in
/// <c>Bifrost.Core</c> instead: it implements a Core interface, touches no Android API, and is
/// therefore reachable from every test project.
/// </para>
/// </remarks>
public sealed class MockTransport(MockScenario? scenario = null) : IPrinterTransport
{
    private readonly MockScenario _scenario = scenario ?? new MockScenario.Ideal();
    private readonly StateStream<ConnectionState> _state = new(new ConnectionState.Disconnected());
    private readonly List<byte[]> _written = [];

    private int _connectAttempts;
    private int _writeAttempts;

    public TransportType Type => TransportType.Mock;

    public IStateStream<ConnectionState> ConnectionState => _state;

    /// <summary>Every payload handed to <see cref="WriteAsync"/>, in order. Assert on this.</summary>
    public IReadOnlyList<byte[]> Written => _written;

    /// <summary>Everything written, concatenated — convenient for golden-output assertions.</summary>
    public byte[] AllBytes => _written.SelectMany(b => b).ToArray();

    public int ConnectAttempts => _connectAttempts;

    public int WriteAttempts => _writeAttempts;

    public Task<Result> ConnectAsync(string address, CancellationToken ct)
    {
        _connectAttempts++;

        if (_scenario is MockScenario.ConnectFails)
        {
            var error = new PrinterError.ConnectionFailed("mock: connect refused");
            _state.Publish(new ConnectionState.Failed(error));
            return Task.FromResult(Result.Fail(error));
        }

        _state.Publish(new ConnectionState.Connected($"MockPrinter({address})", Mtu: null));
        return Task.FromResult(Result.Ok());
    }

    public async Task<Result> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        _writeAttempts++;

        if (_state.Current is not Model.ConnectionState.Connected)
        {
            return Result.Fail(new PrinterError.Disconnected());
        }

        switch (_scenario)
        {
            case MockScenario.OutOfPaper:
                return Result.Fail(new PrinterError.OutOfPaper());

            case MockScenario.CoverOpen:
                return Result.Fail(new PrinterError.CoverOpen());

            case MockScenario.FailNTimesThenSucceed f when _writeAttempts <= f.N:
                return Result.Fail(new PrinterError.TransmitTimeout());

            case MockScenario.DisconnectAfter d when bytes.Length > d.Bytes:
                _written.Add(bytes[..d.Bytes].ToArray());
                _state.Publish(new ConnectionState.Failed(new PrinterError.Disconnected()));
                return Result.Fail(new PrinterError.Disconnected());

            case MockScenario.TruncateAt t when bytes.Length > t.Bytes:
                // The BLE flow-control failure, made deterministic. On real hardware it depends on
                // printer buffer size and timing and is nearly impossible to reproduce on demand.
                // Silently accepting a partial write is the dangerous shape: the caller believes
                // the label printed. DES-06 §7.3.
                _written.Add(bytes[..t.Bytes].ToArray());
                return Result.Ok();

            case MockScenario.SlowWrite s:
                var ms = (int)(bytes.Length * 1000.0 / Math.Max(s.BytesPerSecond, 1));
                try
                {
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result.Fail(new PrinterError.TransmitTimeout());
                }

                break;
        }

        _written.Add(bytes.ToArray());
        return Result.Ok();
    }

    public Task<Result<byte[]>> ReadAsync(TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(Result<byte[]>.Ok(_scenario switch
        {
            // Bit 3 clear = online.
            MockScenario.Ideal => [0b0000_0000],
            MockScenario.OutOfPaper => [0b0000_1000],
            // A write-only printer says nothing. Empty is a fact, not a failure (FR-608).
            _ => [],
        }));

    public Task DisconnectAsync()
    {
        _state.Publish(new ConnectionState.Disconnected());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The ways a printer can misbehave, enumerated so they can be tested on purpose.</summary>
public abstract record MockScenario
{
    /// <summary>Everything works.</summary>
    public sealed record Ideal : MockScenario;

    public sealed record OutOfPaper : MockScenario;

    public sealed record CoverOpen : MockScenario;

    public sealed record ConnectFails : MockScenario;

    /// <summary>Drops the connection partway through a payload.</summary>
    public sealed record DisconnectAfter(int Bytes) : MockScenario;

    /// <summary>Throttles writes so the transmit timeout (FR-609) can be exercised.</summary>
    public sealed record SlowWrite(int BytesPerSecond) : MockScenario;

    /// <summary>Fails the first N writes, then succeeds — for retry and backoff tests.</summary>
    public sealed record FailNTimesThenSucceed(int N) : MockScenario;

    /// <summary>
    /// Accepts only the first N bytes but <b>reports success</b> — the BLE flow-control failure.
    /// The most dangerous shape of all, because nothing looks wrong until the label is read.
    /// </summary>
    public sealed record TruncateAt(int Bytes) : MockScenario;
}
