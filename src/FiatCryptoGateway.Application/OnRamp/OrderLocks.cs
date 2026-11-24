using System.Collections.Concurrent;

namespace FiatCryptoGateway.Application.OnRamp;

/// <summary>
/// Serialises work on each order inside one process, so a webhook and the background worker cannot
/// advance the same order at the same time. A multi-instance deployment needs a database row lock instead.
/// </summary>
public sealed class OrderLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(orderId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
