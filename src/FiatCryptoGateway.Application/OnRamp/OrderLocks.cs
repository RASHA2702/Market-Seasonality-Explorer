using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Application.Ports;

namespace FiatCryptoGateway.Application.OnRamp;

/// <summary>
/// Serialises work on each order inside one process. Only safe with a single API instance; the
/// PostgreSQL store provides a lock that holds across instances.
/// </summary>
public sealed class InMemoryOrderLocks : IOrderLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(orderId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Lock keys for things other than a single order, in the same lock space as order IDs.</summary>
public static class OrderLockKeys
{
    /// <summary>A stable key per customer, so their order creations run one at a time on every instance.</summary>
    public static Guid Customer(string clientId, string customerId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"customer\n{clientId}\n{customerId}")).AsSpan(0, 16));

    /// <summary>One change to a merchant's settlement settings at a time, on every instance.</summary>
    public static Guid Settlement(string clientId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"settlement\n{clientId}")).AsSpan(0, 16));

    /// <summary>One reconciliation of a provider's day at a time, on every instance.</summary>
    public static Guid Reconciliation(string provider, DateOnly day) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"reconciliation\n{provider}\n{day:yyyy-MM-dd}")).AsSpan(0, 16));
}
