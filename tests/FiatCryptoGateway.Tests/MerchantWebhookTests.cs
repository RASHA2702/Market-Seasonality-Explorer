using System.Text.Json;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class MerchantNotificationTests
{
    private static readonly DateTimeOffset Later = Fixtures.Start.AddMinutes(1);

    [Fact]
    public void Notifications_CoverOnlyNewStepsAMerchantCanSee()
    {
        var order = Fixtures.Order();
        var created = MerchantNotifications.ForNewEvents(order, storedEvents: 0);
        var stored = order.Events.Count;
        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order, ComplianceOutcome.ManualReview), Later);
        order.Review(ReviewActionKind.Reject, "officer", "Suspicious.", null, Later);
        order.Review(ReviewActionKind.FreezeAndReport, "officer", "Reported.", "STR-1", Later);
        var held = MerchantNotifications.ForNewEvents(order, stored);
        stored = order.Events.Count;
        order.Review(ReviewActionKind.ReleaseForRefund, "officer", "Release instructed.", null, Later);
        var released = MerchantNotifications.ForNewEvents(order, stored);

        Assert.Equal([("AwaitingFiatPayment", 1)], created.Select(Describe));
        Assert.Equal([("FiatReceived", 2), ("OnHold", 3)], held.Select(Describe));
        // Order event 6 is the merchant's 4th: the hidden rejection and freeze leave no gap.
        var refund = Assert.Single(released);
        Assert.Equal(("RefundPending", 4), Describe(refund));
        Assert.Equal(6, refund.Sequence);
        foreach (var word in new[] { "Frozen", "FROZEN", "REJECT", "Reject", "STR-1", "REPORT", "Compliance", "COMPLIANCE" })
        {
            Assert.All(created.Concat(held).Concat(released), message => Assert.DoesNotContain(word, message.Payload, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Payload_CarriesTheEventIdAndTheMerchantView()
    {
        var order = Fixtures.Order();

        var message = Assert.Single(MerchantNotifications.ForNewEvents(order, storedEvents: 0));

        using var json = JsonDocument.Parse(message.Payload);
        var root = json.RootElement;
        Assert.Equal(message.MessageId, root.GetProperty("id").GetGuid());
        Assert.Equal("order.status_changed", root.GetProperty("type").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(order.OrderId, data.GetProperty("orderId").GetGuid());
        Assert.Equal("AwaitingFiatPayment", data.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("previousStatus").ValueKind);
        Assert.Equal("ORDER_CREATED", data.GetProperty("reason").GetString());
        Assert.Equal((Fixtures.ClientId, "payment_order", order.OrderId), (message.ClientId, message.AggregateType, message.AggregateId));
    }

    private static (string Status, int Sequence) Describe(OutboxMessage message)
    {
        using var json = JsonDocument.Parse(message.Payload);
        var data = json.RootElement.GetProperty("data");
        return (data.GetProperty("status").GetString()!, data.GetProperty("sequence").GetInt32());
    }
}

public sealed class MerchantWebhookDispatcherTests
{
    private const string Secret = "test-only-merchant-webhook-secret-0123456789";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly MerchantWebhookTarget Target = new(new Uri("https://merchant.example/hooks"), Secret);

    [Fact]
    public async Task Notification_IsSignedWithTheMerchantSecretAndDeliveredOnce()
    {
        var harness = new Harness();
        var order = Fixtures.Order();
        await harness.Orders.AddAsync(order, None);

        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None));

        var sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(Target.Url, sent.Url);
        Assert.True(WebhookSignature.Verify(Secret, sent.Signature, sent.Body, harness.Time.GetUtcNow(), TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("another-secret-of-thirty-two-chars!!", sent.Signature, sent.Body, harness.Time.GetUtcNow(), TimeSpan.FromMinutes(5)));
        using var json = JsonDocument.Parse(sent.Body);
        Assert.Equal(sent.EventId, json.RootElement.GetProperty("id").GetGuid());
        var status = Assert.Single(await harness.Outbox.ListForAggregateAsync(order.OrderId, None));
        Assert.Equal((1, harness.Time.GetUtcNow()), (status.Attempts, status.DeliveredAt));
        Assert.Equal(0, await harness.Dispatcher.DispatchDueAsync(None));
    }

    [Fact]
    public async Task EachNotification_IsSignedWhenItIsSent()
    {
        var harness = new Harness();
        harness.Sender.ResponseTime = TimeSpan.FromMinutes(4); // a slow merchant endpoint
        for (var i = 0; i < 3; i++)
        {
            await harness.Orders.AddAsync(Fixtures.Order(), None);
        }

        Assert.Equal(3, await harness.Dispatcher.DispatchDueAsync(None));

        // The third goes out 8 minutes after the batch was claimed, yet its signature is fresh when it arrives.
        Assert.Equal(TimeSpan.FromMinutes(8), harness.Sender.Sent[2].SentAt - Fixtures.Start);
        Assert.All(harness.Sender.Sent, sent =>
            Assert.True(WebhookSignature.Verify(Secret, sent.Signature, sent.Body, sent.SentAt, TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task FailedDelivery_IsRetriedWithBackoffThenGivenUp()
    {
        var harness = new Harness();
        harness.Options.MaxAttempts = 3;
        var order = Fixtures.Order();
        await harness.Orders.AddAsync(order, None);
        for (var i = 0; i < 3; i++)
        {
            harness.Sender.Results.Enqueue(WebhookDeliveryResult.Failed("HTTP_500"));
        }

        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None)); // attempt 1 fails: retry in 30 s
        harness.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await harness.Dispatcher.DispatchDueAsync(None));
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None)); // attempt 2 fails: retry in 60 s
        harness.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await harness.Dispatcher.DispatchDueAsync(None));
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None)); // attempt 3 fails: give up

        var status = Assert.Single(await harness.Outbox.ListForAggregateAsync(order.OrderId, None));
        Assert.Equal((3, "HTTP_500", (DateTimeOffset?)null), (status.Attempts, status.LastError, status.DeliveredAt));
        Assert.Equal(harness.Time.GetUtcNow(), status.DeadAt);
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await harness.Dispatcher.DispatchDueAsync(None));
        Assert.Equal(3, harness.Sender.Sent.Count);
    }

    [Fact]
    public async Task AnOrdersNotifications_GoOutInOrder()
    {
        var harness = new Harness();
        var order = Fixtures.Order();
        await harness.Orders.AddAsync(order, None);
        order.RecordFiatPayment(Fixtures.Payment(order), Fixtures.Start);
        await harness.Orders.SaveAsync(order, [], None);
        harness.Sender.Results.Enqueue(WebhookDeliveryResult.Failed("TIMEOUT"));

        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None)); // the first fails...
        Assert.Equal(0, await harness.Dispatcher.DispatchDueAsync(None)); // ...and the second waits behind it
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None));
        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None));

        Assert.Equal(["AwaitingFiatPayment", "AwaitingFiatPayment", "FiatReceived"], harness.Sender.Sent.Select(sent => StatusOf(sent.Body)));
    }

    [Fact]
    public async Task OneMerchantsFailingEndpoint_DoesNotHoldUpOtherOrders()
    {
        var harness = new Harness();
        var first = Fixtures.Order();
        var second = Fixtures.Order();
        await harness.Orders.AddAsync(first, None);
        await harness.Orders.AddAsync(second, None);
        harness.Sender.Results.Enqueue(WebhookDeliveryResult.Failed("HTTP_503"));

        Assert.Equal(2, await harness.Dispatcher.DispatchDueAsync(None));

        Assert.Single(await harness.Outbox.ListForAggregateAsync(first.OrderId, None), status => status.DeliveredAt is null);
        Assert.Single(await harness.Outbox.ListForAggregateAsync(second.OrderId, None), status => status.DeliveredAt is not null);
    }

    [Fact]
    public async Task MerchantWithoutAWebhook_IsRecordedAndNeverRetried()
    {
        var harness = new Harness(new FixedTargets());
        var order = Fixtures.Order();
        await harness.Orders.AddAsync(order, None);

        await harness.Dispatcher.DispatchDueAsync(None);
        harness.Time.Advance(TimeSpan.FromDays(1));
        await harness.Dispatcher.DispatchDueAsync(None);

        Assert.Empty(harness.Sender.Sent);
        var status = Assert.Single(await harness.Outbox.ListForAggregateAsync(order.OrderId, None));
        Assert.Equal("NO_WEBHOOK_CONFIGURED", status.LastError);
        Assert.NotNull(status.DeadAt);
    }

    [Fact]
    public async Task GivenUpDeliveries_AreListedAndCanBeSentAgainOnce()
    {
        var harness = new Harness(new FixedTargets((Fixtures.ClientId, Target))); // client-b has no webhook
        harness.Options.MaxAttempts = 1;
        var failing = Fixtures.Order();
        var delivered = Fixtures.Order();
        var quoteForB = Domain.Pricing.QuoteCalculator.Calculate(Fixtures.Inputs() with { ClientId = "client-b" });
        var noWebhook = PaymentOrder.Create(Guid.NewGuid(), "client-b", "cust_verified", quoteForB, Fixtures.Address, Fixtures.Start, TimeSpan.FromMinutes(15));
        await harness.Orders.AddAsync(failing, None);
        await harness.Orders.AddAsync(delivered, None);
        await harness.Orders.AddAsync(noWebhook, None);
        harness.Sender.Results.Enqueue(WebhookDeliveryResult.Failed("HTTP_500"));
        await harness.Dispatcher.DispatchDueAsync(None);

        var failed = Assert.Single(await harness.Outbox.ListFailedAsync(Fixtures.Start.AddDays(-1), 10, None));
        Assert.Equal((failing.OrderId, "HTTP_500", 1), (failed.AggregateId, failed.LastError, failed.Attempts));
        Assert.Empty(await harness.Outbox.ListFailedAsync(Fixtures.Start.AddMinutes(1), 10, None)); // only since the given time

        Assert.True(await harness.Outbox.RetryAsync(failed.MessageId, harness.Time.GetUtcNow(), None));
        Assert.False(await harness.Outbox.RetryAsync(failed.MessageId, harness.Time.GetUtcNow(), None)); // already back in line
        Assert.Empty(await harness.Outbox.ListFailedAsync(Fixtures.Start.AddDays(-1), 10, None));
        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None)); // sent again, and delivered this time
        Assert.NotNull(Assert.Single(await harness.Outbox.ListForAggregateAsync(failing.OrderId, None)).DeliveredAt);

        var deliveredId = Assert.Single(await harness.Outbox.ListForAggregateAsync(delivered.OrderId, None)).MessageId;
        Assert.False(await harness.Outbox.RetryAsync(deliveredId, harness.Time.GetUtcNow(), None)); // delivered, not given up
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))] // how an HTTP timeout surfaces
    public async Task SenderException_BecomesARetry(Type exceptionType)
    {
        var harness = new Harness();
        harness.Sender.Throw = (Exception)Activator.CreateInstance(exceptionType)!;
        var order = Fixtures.Order();
        await harness.Orders.AddAsync(order, None);

        Assert.Equal(1, await harness.Dispatcher.DispatchDueAsync(None));

        var status = Assert.Single(await harness.Outbox.ListForAggregateAsync(order.OrderId, None));
        Assert.Equal((1, exceptionType.Name), (status.Attempts, status.LastError));
        Assert.Null(status.DeadAt);
        Assert.Null(status.DeliveredAt);
    }

    [Fact]
    public async Task ClaimedMessages_AreLeasedSoNoOtherInstanceSendsThem()
    {
        var outbox = new InMemoryOutbox();
        var orders = new InMemoryOrderStore(new InMemoryLedgerStore(), outbox);
        await orders.AddAsync(Fixtures.Order(), None);
        await orders.AddAsync(Fixtures.Order(), None);
        var lease = TimeSpan.FromMinutes(5);

        var first = await outbox.ClaimDueAsync(Fixtures.Start, lease, 10, None);
        var second = await outbox.ClaimDueAsync(Fixtures.Start, lease, 10, None);
        // An instance that died mid-send gives its messages back when the lease ends.
        var afterLease = await outbox.ClaimDueAsync(Fixtures.Start + lease, lease, 10, None);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
        Assert.Equal(first.Select(message => message.MessageId).Order(), afterLease.Select(message => message.MessageId).Order());
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(7, 1_920)]
    [InlineData(8, 3_600)]
    [InlineData(40, 3_600)]
    public void Backoff_DoublesUpToTheCap(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), MerchantWebhookDispatcher.Backoff(attempt, new WebhookOptions()));
    }

    [Fact]
    public void DefaultSettings_AreValid()
    {
        Assert.True(new WebhookOptionsValidator().Validate(null, new WebhookOptions()).Succeeded);
    }

    [Theory]
    [InlineData(200, 20, 10)] // a batch of 20 ten-second timeouts outlasts a 200-second lease
    [InlineData(300, 0, 10)]
    [InlineData(300, 20, 0)]
    public void SettingsThatCouldDoubleSend_AreRefused(int leaseSeconds, int batchSize, int timeoutSeconds)
    {
        var options = new WebhookOptions { LeaseSeconds = leaseSeconds, BatchSize = batchSize, SendTimeoutSeconds = timeoutSeconds };

        Assert.True(new WebhookOptionsValidator().Validate(null, options).Failed);
    }

    private static string StatusOf(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").GetProperty("status").GetString()!;
    }

    private sealed class Harness
    {
        public Harness(IMerchantWebhookTargets? targets = null)
        {
            Sender = new RecordingSender(Time);
            Orders = new InMemoryOrderStore(new InMemoryLedgerStore(), Outbox);
            Dispatcher = new MerchantWebhookDispatcher(
                Outbox,
                targets ?? new FixedTargets((Fixtures.ClientId, Target)),
                Sender,
                Microsoft.Extensions.Options.Options.Create(Options),
                Time,
                NullLogger<MerchantWebhookDispatcher>.Instance);
        }

        public ManualTimeProvider Time { get; } = new(Fixtures.Start);

        public InMemoryOutbox Outbox { get; } = new();

        public InMemoryOrderStore Orders { get; }

        public RecordingSender Sender { get; }

        public WebhookOptions Options { get; } = new();

        public MerchantWebhookDispatcher Dispatcher { get; }
    }

    private sealed class FixedTargets(params (string ClientId, MerchantWebhookTarget Target)[] targets) : IMerchantWebhookTargets
    {
        public MerchantWebhookTarget? Find(string clientId) =>
            targets.Where(entry => entry.ClientId == clientId).Select(entry => entry.Target).FirstOrDefault();
    }

    private sealed class RecordingSender(ManualTimeProvider time) : IWebhookSender
    {
        public List<(Uri Url, Guid EventId, string Body, string Signature, DateTimeOffset SentAt)> Sent { get; } = [];

        public Queue<WebhookDeliveryResult> Results { get; } = new();

        public Exception? Throw { get; set; }

        /// <summary>How long the merchant's endpoint takes to answer.</summary>
        public TimeSpan ResponseTime { get; set; }

        public Task<WebhookDeliveryResult> SendAsync(Uri url, Guid eventId, string body, string signature, CancellationToken cancellationToken)
        {
            Sent.Add((url, eventId, body, signature, time.GetUtcNow()));
            time.Advance(ResponseTime);
            return Throw is not null
                ? Task.FromException<WebhookDeliveryResult>(Throw)
                : Task.FromResult(Results.Count > 0 ? Results.Dequeue() : WebhookDeliveryResult.Success);
        }
    }
}
