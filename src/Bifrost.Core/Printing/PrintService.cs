using Bifrost.Core.Model;
using Bifrost.Core.Payload;
using Bifrost.Core.Threading;

namespace Bifrost.Core.Printing;

/// <summary>
/// Compile → drive → transmit. The whole print path in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Demo scope.</b> This is the thin stand-in for what becomes <c>PrintWorker</c> plus a durable
/// queue in Phase 2 (ADR-005). It prints synchronously with no persistence, no retry and no
/// idempotency — the debt the demo plan knowingly takes on.
/// </para>
/// <para>
/// The single-writer discipline is kept even so: one job transmits at a time. Two half-labels
/// printed on top of each other is not a failure worth risking to save a semaphore.
/// </para>
/// </remarks>
public sealed class PrintService(
    IPrinterTransport transport,
    IPrinterDriver driver,
    PrinterProfile profile)
{
    private static readonly TimeSpan TransmitTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _jobCounter;

    public PrinterProfile Profile => profile;

    public IPrinterDriver Driver => driver;

    public IStateStream<ConnectionState> ConnectionState => transport.ConnectionState;

    public Task<Result> ConnectAsync(string address, CancellationToken ct) =>
        transport.ConnectAsync(address, ct);

    public async Task<Result<PrintJob>> PrintAsync(PrintRequestDto request, CancellationToken ct)
    {
        var compiler = new DslCompiler(profile.PrintWidthDots, profile.MediaType);

        var compiled = compiler.Compile(request);
        if (compiled.IsFailure) return compiled.Error;

        return await PrintAsync(compiled.Value, ct).ConfigureAwait(false);
    }

    public async Task<Result<PrintJob>> PrintAsync(PrintDocument document, CancellationToken ct)
    {
        var id = $"job_{Interlocked.Increment(ref _jobCounter):D6}";

        if (transport.ConnectionState.Current is not Model.ConnectionState.Connected)
        {
            return new PrinterError.NotConnected();
        }

        byte[] bytes;
        try
        {
            bytes = driver.Serialise(document, profile);
        }
        catch (NotSupportedException ex)
        {
            // A driver refusing a block it never declared support for. Permanent: retrying an
            // unsupported element cannot help (FR-107).
            return new PrinterError.UnsupportedElement(ex.Message);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TransmitTimeout);

            var written = await transport.WriteAsync(bytes, cts.Token).ConfigureAwait(false);

            return written.IsFailure
                ? written.Error
                : Result<PrintJob>.Ok(new PrintJob(id, JobState.Printed, bytes.Length));
        }
        finally
        {
            _gate.Release();
        }
    }
}

public enum JobState { Queued, Printed, Failed }

public sealed record PrintJob(string JobId, JobState State, int ByteCount);
