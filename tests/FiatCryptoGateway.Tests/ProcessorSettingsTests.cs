using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

/// <summary>A rail that only says what it is. Anything that tries to use it fails, so a test cannot depend on it by accident.</summary>
public sealed class DescribedRail(string name, PaymentMethod method, bool isSandbox) : IPaymentRail
{
    public string Name => name;

    public PaymentMethod Method => method;

    public bool IsSandbox => isSandbox;

    public InstrumentCheck Check(PaymentInstrumentInput input, DateTimeOffset now) => throw new NotSupportedException();

    public Task<RailResponse> StartAsync(RailPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RailResponse> CompleteAuthenticationAsync(string railReference, AuthenticationResponse response, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<DateTimeOffset> CaptureAsync(string railReference, Money amount, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DateTimeOffset> RefundAsync(string railReference, Money amount, string idempotencyKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public sealed class ProcessorSettingsTests
{
    private static readonly ManualTimeProvider Time = new(Fixtures.Start);

    [Fact]
    public void TheDefaults_AreValid_AndTheGatewaysOwnProcessorIsTheDefaultProvider()
    {
        Assert.True(new ProcessorOptionsValidator().Validate(null, new ProcessorOptions()).Succeeded);
        Assert.True(new FiatProviderOptionsValidator().Validate(null, new FiatProviderOptions()).Succeeded);
        Assert.Equal(FiatProviderChoice.Processor, new FiatProviderOptions().Provider);
    }

    [Theory]
    [InlineData("AttemptWindowMinutes", "0")]
    [InlineData("AttemptWindowMinutes", "61")]
    [InlineData("AuthenticationTries", "0")]
    [InlineData("AuthenticationTries", "11")]
    [InlineData("MaxAttemptsPerOrder", "0")]
    [InlineData("MaxAttemptsPerOrder", "101")]
    [InlineData("StuckAfterSeconds", "4")]
    [InlineData("StuckAfterSeconds", "3601")]
    [InlineData("AuthorizationLapseHours", "0")]
    [InlineData("AuthorizationLapseHours", "169")]
    [InlineData("SweepBatchSize", "0")]
    [InlineData("SweepBatchSize", "1001")]
    public void ASettingOutsideItsRange_IsRefused_AndNamed(string setting, string value)
    {
        var options = Bind(setting, value);

        var result = new ProcessorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(setting, Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AttemptWindowMinutes", "1")]
    [InlineData("AttemptWindowMinutes", "60")]
    [InlineData("AuthenticationTries", "1")]
    [InlineData("AuthenticationTries", "10")]
    [InlineData("MaxAttemptsPerOrder", "1")]
    [InlineData("MaxAttemptsPerOrder", "100")]
    [InlineData("StuckAfterSeconds", "5")]
    [InlineData("StuckAfterSeconds", "3600")]
    [InlineData("AuthorizationLapseHours", "1")]
    [InlineData("AuthorizationLapseHours", "168")]
    [InlineData("SweepBatchSize", "1")]
    [InlineData("SweepBatchSize", "1000")]
    public void ASettingAtTheEdgeOfItsRange_IsAccepted(string setting, string value) =>
        Assert.True(new ProcessorOptionsValidator().Validate(null, Bind(setting, value)).Succeeded);

    [Theory]
    [InlineData("Processor", FiatProviderChoice.Processor)]
    [InlineData("SandboxPartner", FiatProviderChoice.SandboxPartner)]
    [InlineData("sandboxpartner", FiatProviderChoice.SandboxPartner)]
    public void TheProviderChoice_IsReadByName(string value, FiatProviderChoice expected)
    {
        var options = new FiatProviderOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Provider"] = value }).Build().Bind(options);

        Assert.Equal(expected, options.Provider);
        Assert.True(new FiatProviderOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void ANumberThatIsNoProvider_IsRefused()
    {
        var options = new FiatProviderOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Provider"] = "7" }).Build().Bind(options);

        Assert.True(new FiatProviderOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void AnUnknownProviderName_StopsTheSettingsFromBeingRead()
    {
        using var services = Compose("Banana");

        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IOptions<FiatProviderOptions>>().Value);
    }

    [Theory]
    [InlineData(null, typeof(GatewayProcessorProvider))]
    [InlineData("Processor", typeof(GatewayProcessorProvider))]
    [InlineData("SandboxPartner", typeof(SandboxFiatProvider))]
    public void TheChosenProvider_IsTheOrderFlowsProvider_AndTheOnlyReportedSource(string? choice, Type expected)
    {
        using var services = Compose(choice);

        var provider = services.GetRequiredService<IFiatPaymentProvider>();
        var source = Assert.Single(services.GetServices<ISettlementReportSource>());

        Assert.IsType(expected, provider);
        Assert.Same(provider, source);
        Assert.Same(provider, services.GetRequiredService<OnRampProviders>().Fiat);
    }

    // ---- the sandbox-only guard for payment rails ----

    [Fact]
    public void TheSandboxRails_PassTheGuard()
    {
        var result = new PaymentRailGuard([new SandboxCardRail(Time), new SandboxUpiRail(Time)]).Validate(null, new ProcessorOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ARailThatIsNotASandboxRail_IsRefused_ByName()
    {
        var result = new PaymentRailGuard([new SandboxCardRail(Time), new DescribedRail("live-upi-network", PaymentMethod.Upi, isSandbox: false)])
            .Validate(null, new ProcessorOptions());

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("live-upi-network", failure, StringComparison.Ordinal);
        Assert.Contains("not a sandbox rail", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoRailsForOneMethod_AreRefused()
    {
        var result = new PaymentRailGuard([new SandboxCardRail(Time), new SandboxCardRail(Time), new SandboxUpiRail(Time)]).Validate(null, new ProcessorOptions());

        Assert.True(result.Failed);
        Assert.Contains("More than one payment rail is registered for Card", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void NoRails_AreRefused()
    {
        var result = new PaymentRailGuard([]).Validate(null, new ProcessorOptions());

        Assert.True(result.Failed);
        Assert.Contains("No payment rail is registered", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateway_RefusesToStart_WithARailThatIsNotASandboxRail()
    {
        using var services = Compose(null, new DescribedRail("live-card-network", PaymentMethod.Card, isSandbox: false));

        var failure = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ProcessorOptions>>().Value);

        Assert.Contains("live-card-network", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailSet_SurvivesBeingBuiltBeforeTheGuardRuns_AndFindsRailsByMethod()
    {
        var first = new SandboxCardRail(Time);
        var set = new PaymentRailSet([first, new SandboxCardRail(Time), new SandboxUpiRail(Time)]);

        Assert.Same(first, set.For(PaymentMethod.Card));
        Assert.Equal(PaymentMethod.Upi, set.Require(PaymentMethod.Upi).Method);
        Assert.Equal(2, set.All.Count);
        Assert.Null(new PaymentRailSet([]).For(PaymentMethod.Card));
        Assert.Equal("PAYMENT_METHOD_UNAVAILABLE", Assert.Throws<Domain.GatewayRuleException>(() => new PaymentRailSet([]).Require(PaymentMethod.Upi)).Code);
    }

    // ---- identifiers and locks ----

    [Fact]
    public void ThePaymentLock_IsStable_AndNeverTheOrdersOwnLock()
    {
        var order = Guid.NewGuid();

        Assert.Equal(OrderLockKeys.Payment(order), OrderLockKeys.Payment(order));
        Assert.NotEqual(order, OrderLockKeys.Payment(order)); // recording a payment takes the order's own lock; the two must differ
        Assert.NotEqual(OrderLockKeys.Payment(order), OrderLockKeys.Payment(Guid.NewGuid()));
    }

    [Fact]
    public void PaymentAndRefundIdentifiers_FollowTheAttempt_SoARetryMakesTheSameOne()
    {
        var attempt = Guid.NewGuid();

        Assert.Equal(PaymentIdentifiers.Payment(attempt), PaymentIdentifiers.Payment(attempt));
        Assert.Equal($"gp_pay_{attempt:N}", PaymentIdentifiers.Payment(attempt));
        Assert.Equal($"gp_rfd_{attempt:N}", PaymentIdentifiers.Refund(attempt));
        Assert.NotEqual(PaymentIdentifiers.Payment(attempt), PaymentIdentifiers.Payment(Guid.NewGuid()));
    }

    private static ProcessorOptions Bind(string setting, string value)
    {
        var options = new ProcessorOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [setting] = value }).Build().Bind(options);
        return options;
    }

    private static ServiceProvider Compose(string? fiatProvider, IPaymentRail? extraRail = null)
    {
        var settings = new Dictionary<string, string?>();
        if (fiatProvider is not null)
        {
            settings["Gateway:Fiat:Provider"] = fiatProvider;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        if (extraRail is not null)
        {
            services.AddSingleton(extraRail);
        }

        return services.BuildServiceProvider(validateScopes: true);
    }
}
