using FiatCryptoGateway.Application.Checkout;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Domain.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class CheckoutLinkTests
{
    private const string Secret = "test-only-checkout-link-secret-0123456789";

    [Fact]
    public void Link_PointsAtTheHostedPageAndLeadsBackToItsOrder()
    {
        var links = Links();
        var orderId = Guid.NewGuid();

        var url = links.Url(orderId);

        Assert.Equal("https", url.Scheme);
        Assert.StartsWith("https://pay.example.com/gateway/checkout/", url.ToString(), StringComparison.Ordinal); // keeps a path prefix
        Assert.True(links.TryRead(url.Segments[^1], out var read));
        Assert.Equal(orderId, read);
    }

    [Fact]
    public void AlteredOrForeignLinks_AreRefused()
    {
        var links = Links();
        var token = links.Token(Guid.NewGuid());
        var otherOrder = Guid.NewGuid().ToString("N") + token[32..];          // another order with this link's signature
        var flipped = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');           // signature changed
        var foreign = Links("another-secret-with-at-least-32-characters").Token(Guid.NewGuid());

        Assert.False(links.TryRead(otherOrder, out _));
        Assert.False(links.TryRead(flipped, out _));
        Assert.False(links.TryRead(foreign, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-link")]
    [InlineData("0123456789abcdef0123456789abcdef")]   // no signature
    [InlineData("0123456789abcdef0123456789abcdef.")]
    [InlineData("0123456789abcdef0123456789abcdeZ.AAAA")]
    [InlineData("0123456789abcdef0123456789abcdef.!!!!")]
    public void MalformedLinks_AreRefused(string? token)
    {
        Assert.False(Links().TryRead(token, out _));
    }

    [Fact]
    public void WithoutAConfiguredSecret_LinksStillCannotBeForged()
    {
        var first = Links(secret: string.Empty);
        var second = Links(secret: string.Empty);
        var orderId = Guid.NewGuid();

        Assert.True(first.TryRead(first.Token(orderId), out _));
        Assert.False(second.TryRead(first.Token(orderId), out _)); // each gets its own random secret
    }

    [Theory]
    [InlineData("BaseUrl", "http://pay.example.com")]       // plain HTTP away from this machine
    [InlineData("BaseUrl", "https://pay.example.com/?a=b")]
    [InlineData("LinkSecret", "too-short")]
    [InlineData("TimeZone", "Mars/Olympus_Mons")]
    public void InvalidCheckoutSettings_AreRefused(string setting, string value)
    {
        var options = new CheckoutOptions { BaseUrl = new Uri("https://pay.example.com"), LinkSecret = Secret };
        switch (setting)
        {
            case "BaseUrl":
                options.BaseUrl = new Uri(value);
                break;
            case "LinkSecret":
                options.LinkSecret = value;
                break;
            default:
                options.TimeZone = value;
                break;
        }

        Assert.True(new CheckoutOptionsValidator().Validate(null, options).Failed);
        Assert.True(new CheckoutOptionsValidator().Validate(null, new CheckoutOptions()).Succeeded);
    }

    private static CheckoutLinks Links(string secret = Secret) =>
        new(Options.Create(new CheckoutOptions { BaseUrl = new Uri("https://pay.example.com/gateway"), LinkSecret = secret }), NullLogger<CheckoutLinks>.Instance);
}

public sealed class CustomerViewTests
{
    public static TheoryData<OrderStatus> AllStatuses => new(Enum.GetValues<OrderStatus>());

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void EveryOrderStatus_HasACustomerView(OrderStatus status)
    {
        Assert.True(Enum.IsDefined(CustomerView.Status(status)));
    }

    [Theory]
    [InlineData(OrderStatus.ComplianceReview)]
    [InlineData(OrderStatus.ComplianceRejected)]
    [InlineData(OrderStatus.Frozen)]
    [InlineData(OrderStatus.CryptoSent)]
    [InlineData(OrderStatus.Failed)]
    public void ComplianceAndProcessingSteps_LookLikeAnyReceivedPayment(OrderStatus status)
    {
        Assert.Equal(CustomerPaymentStatus.PaymentReceived, CustomerView.Status(status));
    }

    [Fact]
    public void UnpaidOrderPastItsDeadline_IsShownAsExpiredStraightAway()
    {
        var order = Fixtures.Order();

        Assert.Equal(CustomerPaymentStatus.AwaitingPayment, CustomerView.Status(order, order.PaymentDeadline));
        Assert.Equal(CustomerPaymentStatus.Expired, CustomerView.Status(order, order.PaymentDeadline.AddSeconds(1)));
    }
}
