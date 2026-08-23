using System.Threading.Channels;

namespace Bifrost.Core.Threading;

/// <summary>
/// A current value plus a stream of subsequent changes — the project's equivalent of Kotlin's
/// <c>StateFlow</c>.
/// </summary>
/// <remarks>
/// Built on <see cref="System.Threading.Channels"/> rather than taking a reactive-extensions
/// dependency for a single concept (IMP-01 §2.3).
/// </remarks>
public interface IStateStream<T>
{
    /// <summary>The value right now. Always available, never blocks.</summary>
    T Current { get; }

    /// <summary>Values published after subscription. Does not replay <see cref="Current"/>.</summary>
    IAsyncEnumerable<T> WatchAsync(CancellationToken ct);
}

/// <summary>Writable <see cref="IStateStream{T}"/>. Owned by whoever publishes the state.</summary>
public sealed class StateStream<T>(T initial) : IStateStream<T>
{
    private readonly Lock _gate = new();
    private readonly List<Channel<T>> _subscribers = [];
    private T _current = initial;

    public T Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public void Publish(T value)
    {
        Channel<T>[] targets;

        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(_current, value)) return;
            _current = value;
            targets = [.. _subscribers];
        }

        // Unbounded + DropWrite semantics: a slow subscriber must never stall the printer.
        foreach (var channel in targets) channel.Writer.TryWrite(value);
    }

    public async IAsyncEnumerable<T> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_gate) _subscribers.Add(channel);

        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return value;
            }
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }
}
