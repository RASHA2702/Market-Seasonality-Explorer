using System.Collections.Concurrent;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Domain.Settlement;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FiatCryptoGateway.Tests;

/// <summary>Everything the processor logged, kept so a test can check that nothing a customer typed was written down.</summary>
public sealed class LogSink
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Add(string line) => _lines.Enqueue(line);
}

public sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        sink.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// Watches the order locks of one test, which runs one thing at a time. It records which locks are held, and every time
/// an order's own lock is taken while that order's payment lock is held, or the other way round: two flows doing that in
/// opposite orders is how two locks deadlock.
/// </summary>
public sealed class LockWatch(IOrderLocks inner) : IOrderLocks
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _held = [];
    private readonly Dictionary<Guid, int> _acquired = [];
    private readonly List<string> _nested = [];

    /// <summary>Each time an order's lock and its payment lock were held together.</summary>
    public IReadOnlyList<string> Nested
    {
        get
        {
            lock (_gate)
            {
                return [.. _nested];
            }
        }
    }

    public bool IsHeld(Guid key)
    {
        lock (_gate)
        {
            return _held.GetValueOrDefault(key) > 0;
        }
    }

    public int Acquisitions(Guid key)
    {
        lock (_gate)
        {
            return _acquired.GetValueOrDefault(key);
        }
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var heldKeys = _held.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToList();
            if (heldKeys.Contains(OrderLockKeys.Payment(orderId)) || heldKeys.Any(key => OrderLockKeys.Payment(key) == orderId))
            {
                _nested.Add($"Lock {orderId:N} was taken while the order's other lock was held.");
            }
        }

        var lease = await inner.AcquireAsync(orderId, cancellationToken);
        lock (_gate)
        {
            _held[orderId] = _held.GetValueOrDefault(orderId) + 1;
            _acquired[orderId] = _acquired.GetValueOrDefault(orderId) + 1;
        }

        return new Lease(this, lease, orderId);
    }

    private sealed class Lease(LockWatch watch, IAsyncDisposable inner, Guid key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            lock (watch._gate)
            {
                watch._held[key]--;
            }

            await inner.DisposeAsync();
        }
    }
}

/// <summary>The processor's records with a save that can be made to fail once, as a database outage would.</summary>
public sealed class FlakyAttemptStore(IPaymentAttemptStore inner) : IPaymentAttemptStore
{
    /// <summary>The next save of an attempt this matches fails, and then this is cleared.</summary>
    public Func<PaymentAttempt, bool>? FailNextSaveWhen { get; set; }

    public Task AddAsync(PaymentAttempt attempt, CancellationToken cancellationToken) => inner.AddAsync(attempt, cancellationToken);

    public Task SaveAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (FailNextSaveWhen is { } fails && fails(attempt))
        {
            FailNextSaveWhen = null;
            throw new TimeoutException("The database did not answer.");
        }

        return inner.SaveAsync(attempt, cancellationToken);
    }

    public Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken) => inner.FindAsync(attemptId, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken) => inner.ListForOrderAsync(orderId, cancellationToken);

    public Task<PaymentAttempt?> FindCapturedForOrderAsync(Guid orderId, CancellationToken cancellationToken) => inner.FindCapturedForOrderAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListCapturedNotRecordedAsync(int limit, CancellationToken cancellationToken) =>
        inner.ListCapturedNotRecordedAsync(limit, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListStuckAuthorizedAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken) =>
        inner.ListStuckAuthorizedAsync(cutoff, limit, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListPendingExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        inner.ListPendingExpiredAsync(now, limit, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListSettledBetweenAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        inner.ListSettledBetweenAsync(windowStart, windowEnd, cancellationToken);
}

/// <summary>The exchange, which can be made to fail, and which tells a test when the order flow reaches it.</summary>
public sealed class ProbingLiquidity(ILiquidityProvider inner) : ILiquidityProvider
{
    /// <summary>How many of the next purchases fail with a network error.</summary>
    public int FailBuys { get; set; }

    /// <summary>Called as the order flow reaches the exchange.</summary>
    public Action<PaymentOrder>? OnBuy { get; set; }

    public string Name => inner.Name;

    public Task<LiquidityExecution> BuyAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        OnBuy?.Invoke(order);
        if (FailBuys > 0)
        {
            FailBuys--;
            throw new HttpRequestException("The exchange did not answer.");
        }

        return inner.BuyAsync(order, cancellationToken);
    }
}

/// <summary>A payment rail that can be made to fail when capturing, tells a test when it is asked, and counts captures.</summary>
public sealed class FlakyRail(IPaymentRail inner) : IPaymentRail
{
    private int _captureCalls;

    /// <summary>How many of the next captures fail with a network error.</summary>
    public int FailCaptures { get; set; }

    /// <summary>Called as the processor asks the network to start a payment.</summary>
    public Action? OnStart { get; set; }

    /// <summary>Called as the processor asks the network to capture a payment.</summary>
    public Action? OnCapture { get; set; }

    public int CaptureCalls => Volatile.Read(ref _captureCalls);

    public string Name => inner.Name;

    public PaymentMethod Method => inner.Method;

    public bool IsSandbox => inner.IsSandbox;

    public InstrumentCheck Check(PaymentInstrumentInput input, DateTimeOffset now) => inner.Check(input, now);

    public Task<RailResponse> StartAsync(RailPaymentRequest request, CancellationToken cancellationToken)
    {
        OnStart?.Invoke();
        return inner.StartAsync(request, cancellationToken);
    }

    public Task<RailResponse> CompleteAuthenticationAsync(string railReference, AuthenticationResponse response, CancellationToken cancellationToken) =>
        inner.CompleteAuthenticationAsync(railReference, response, cancellationToken);

    public Task<DateTimeOffset> CaptureAsync(string railReference, Money amount, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _captureCalls);
        OnCapture?.Invoke();
        if (FailCaptures > 0)
        {
            FailCaptures--;
            throw new HttpRequestException("The payment network did not answer.");
        }

        return inner.CaptureAsync(railReference, amount, cancellationToken);
    }

    public Task<DateTimeOffset> RefundAsync(string railReference, Money amount, string idempotencyKey, CancellationToken cancellationToken) =>
        inner.RefundAsync(railReference, amount, idempotencyKey, cancellationToken);
}

/// <summary>
/// The gateway's own processor with the real services, the sandbox networks (wrapped so a capture can be made to fail)
/// and in-memory storage, on a clock the test moves.
/// </summary>
public sealed class ProcessorHarness : IDisposable
{
    public const string Client = "client-a";
    public const string Address = "0x1111111111111111111111111111111111111111";

    private static readonly CancellationToken None = CancellationToken.None;

    public ProcessorHarness(IDictionary<string, string?>? settings = null)
    {
        // New settlement wallets are usable at once here; the cooling-off period has its own tests.
        var values = new Dictionary<string, string?> { ["Gateway:Settlement:CoolingOffHours"] = "0" };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        CardRail = new FlakyRail(new SandboxCardRail(Time));
        UpiRail = new FlakyRail(new SandboxUpiRail(Time));
        Locks = new LockWatch(new InMemoryOrderLocks());
        AttemptStore = new FlakyAttemptStore(new InMemoryPaymentAttemptStore());
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        services.RemoveAll<IPaymentRail>();
        services.AddSingleton<IPaymentRail>(CardRail);
        services.AddSingleton<IPaymentRail>(UpiRail);
        services.RemoveAll<IOrderLocks>();
        services.AddSingleton<IOrderLocks>(Locks);
        services.RemoveAll<IPaymentAttemptStore>();
        services.AddSingleton<IPaymentAttemptStore>(AttemptStore);
        services.RemoveAll<ILiquidityProvider>();
        services.AddSingleton<ILiquidityProvider>(provider => new ProbingLiquidity(provider.GetRequiredService<SandboxLiquidityProvider>()));
        Services = services.BuildServiceProvider(validateScopes: true);
    }

    public ManualTimeProvider Time { get; } = new(Fixtures.Start);

    public LogSink Logs { get; } = new();

    public FlakyRail CardRail { get; }

    public FlakyRail UpiRail { get; }

    public LockWatch Locks { get; }

    public FlakyAttemptStore AttemptStore { get; }

    public ServiceProvider Services { get; }

    public ProbingLiquidity Liquidity => (ProbingLiquidity)Services.GetRequiredService<ILiquidityProvider>();

    public PaymentProcessor Processor => Services.GetRequiredService<PaymentProcessor>();

    public GatewayProcessorProvider Provider => Services.GetRequiredService<GatewayProcessorProvider>();

    public IPaymentAttemptStore Attempts => AttemptStore;

    public IOrderStore Orders => Services.GetRequiredService<IOrderStore>();

    public OnRampService OnRamp => Services.GetRequiredService<OnRampService>();

    public ILedgerStore Ledger => Services.GetRequiredService<ILedgerStore>();

    public void Dispose() => Services.Dispose();

    public static CardInput Card(string number, string securityCode = "737") => new(number, 12, 2028, securityCode, "Asha Verma");

    /// <summary>Creates an order paying out to <paramref name="address"/>, made the merchant's active wallet first.</summary>
    public async Task<PaymentOrder> CreateOrderAsync(decimal amount = 10_000m, string currency = "INR", string address = Address)
    {
        var settlement = Services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(Client, "ethereum", address, null, None);
        if (wallet.Status == SettlementWalletStatus.UnderReview)
        {
            await settlement.ApproveAsync(wallet.WalletId, "officer", "Test wallet, approved for this scenario.", None);
        }

        var quote = await OnRamp.CreateQuoteAsync(Client, new CreateQuoteCommand(currency, amount, "USDC@ethereum"), None);
        return await OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);
    }

    public Task<PaymentAttemptView> StartCardAsync(PaymentOrder order, string number, string securityCode = "737") =>
        Processor.StartAsync(order.OrderId, Card(number, securityCode), None);

    public Task<PaymentAttemptView> StartUpiAsync(PaymentOrder order, string address = SandboxTestInstruments.SuccessfulUpiAddress) =>
        Processor.StartAsync(order.OrderId, new UpiInput(address), None);

    public Task<PaymentAttemptView> AnswerAsync(PaymentOrder order, PaymentAttemptView attempt, AuthenticationResponse answer) =>
        Processor.AuthenticateAsync(order.OrderId, attempt.AttemptId, answer, None);

    public Task<PaymentAttemptView> EnterCodeAsync(PaymentOrder order, PaymentAttemptView attempt, string code = SandboxTestInstruments.Otp) =>
        AnswerAsync(order, attempt, new OtpResponse(code));

    public Task<PaymentAttemptView> ApproveAsync(PaymentOrder order, PaymentAttemptView attempt) =>
        AnswerAsync(order, attempt, new ApprovalResponse(approved: true));

    public async Task<PaymentAttempt> StoredAsync(PaymentAttemptView view) =>
        await Attempts.FindAsync(view.AttemptId, None) ?? throw new InvalidOperationException("The attempt is not stored.");

    public async Task<int> LedgerEntriesAsync(PaymentOrder order, string kind) =>
        (await Ledger.ListAsync(order.OrderId, None)).Count(entry => entry.Kind == kind);

    /// <summary>An attempt that ends captured but not recorded on its order, as if the process stopped between the two.</summary>
    public async Task<PaymentAttempt> SeedCapturedAsync(PaymentOrder order)
    {
        var attempt = NewAttempt(order);
        await Attempts.AddAsync(attempt, None);
        attempt.Authorize("upi_seeded", Time.GetUtcNow());
        attempt.Capture(PaymentIdentifiers.Payment(attempt.AttemptId), Time.GetUtcNow());
        await Attempts.SaveAsync(attempt, None);
        return attempt;
    }

    /// <summary>
    /// An attempt the UPI network approved but that was never captured, as if the process stopped in between. With
    /// <paramref name="networkKnowsIt"/> false the network has no record of it, so capturing it always fails.
    /// </summary>
    public async Task<PaymentAttempt> SeedApprovedAsync(PaymentOrder order, bool networkKnowsIt = true)
    {
        var attempt = NewAttempt(order);
        await Attempts.AddAsync(attempt, None);
        var reference = "upi_unknown";
        if (networkKnowsIt)
        {
            var started = await UpiRail.StartAsync(new RailPaymentRequest(attempt.AttemptId, order.OrderId, attempt.Amount, new UpiInput("success@sandbox")), None);
            var approved = await UpiRail.CompleteAuthenticationAsync(started.RailReference!, new ApprovalResponse(approved: true), None);
            reference = approved.RailReference!;
        }

        attempt.Authorize(reference, Time.GetUtcNow());
        await Attempts.SaveAsync(attempt, None);
        return attempt;
    }

    private PaymentAttempt NewAttempt(PaymentOrder order) =>
        PaymentAttempt.Start(
            Guid.NewGuid(),
            order.OrderId,
            order.ClientId,
            order.Quote.SourceAmount,
            new PaymentInstrument(PaymentMethod.Upi, SandboxTestInstruments.SuccessfulUpiAddress),
            Time.GetUtcNow(),
            TimeSpan.FromMinutes(10));
}
