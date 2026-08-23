using Bifrost.Core.Model;
using Bifrost.Core.Threading;

namespace Bifrost.Core.Printing;

/// <summary>
/// Moves bytes to the printer. Knows nothing about command languages.
/// </summary>
/// <remarks>
/// <see cref="WriteAsync"/> accepts a whole payload — <b>callers never see the MTU</b>. Chunking,
/// MTU negotiation and flow control live entirely inside the BLE implementation; no other component
/// knows the MTU exists (FR-602, FR-604, ADR-007 invariant 3).
/// </remarks>
public interface IPrinterTransport : IAsyncDisposable
{
    TransportType Type { get; }

    IStateStream<ConnectionState> ConnectionState { get; }

    Task<Result> ConnectAsync(string address, CancellationToken ct);

    /// <summary>Write a complete payload. Chunking, if any, is internal.</summary>
    Task<Result> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    Task<Result<byte[]>> ReadAsync(TimeSpan timeout, CancellationToken ct);

    Task DisconnectAsync();
}
