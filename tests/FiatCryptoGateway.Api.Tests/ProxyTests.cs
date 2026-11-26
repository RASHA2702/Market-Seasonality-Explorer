using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>
/// Behind the HTTPS proxy every request comes from the proxy's address. The API must still tell clients apart (the
/// sign-in limit is per client), but only by what the trusted proxy says, never by what a client claims.
/// </summary>
public sealed class ProxyTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private const string Proxy = "10.9.0.2";

    [Fact]
    public async Task BehindTheTrustedProxy_EachClientHasItsOwnSignInLimit()
    {
        using var host = Host(trustedNetworks: "10.9.0.0/16");

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: Proxy, forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: Proxy, forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(host, from: Proxy, forwardedFor: "203.0.113.7"));

        // Another person behind the same proxy is not held back by the first.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: Proxy, forwardedFor: "198.51.100.9"));
    }

    [Fact]
    public async Task AProxyAddressSeenInIPv6Form_IsStillTheTrustedProxy()
    {
        // Kestrel listens on IPv6 and IPv4 at once, so in a container it reports the proxy as ::ffff:10.9.0.2.
        using var host = Host(trustedNetworks: "10.9.0.0/16");

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "::ffff:" + Proxy, forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "::ffff:" + Proxy, forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(host, from: "::ffff:" + Proxy, forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "::ffff:" + Proxy, forwardedFor: "198.51.100.9"));
    }

    [Fact]
    public async Task FromAnywhereElse_AClaimedAddressIsIgnored()
    {
        using var host = Host(trustedNetworks: "10.9.0.0/16");

        // A client that is not the proxy cannot dodge the limit by claiming a new address each time.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "192.0.2.50", forwardedFor: "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "192.0.2.50", forwardedFor: "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(host, from: "192.0.2.50", forwardedFor: "203.0.113.3"));

        // Nor can this machine: the usual default of trusting loopback is dropped once a proxy network is set.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.4"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.5"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.6"));
    }

    [Fact]
    public async Task WithoutATrustedProxy_EvenThisMachineCannotClaimAnotherAddress()
    {
        using var host = Host(trustedNetworks: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(host, from: "127.0.0.1", forwardedFor: "203.0.113.3"));
    }

    private static async Task<HttpStatusCode> SignInAsync(WebApplicationFactory<Program> host, string from, string forwardedFor)
    {
        var context = await host.Server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Post;
            http.Request.Path = "/v1/review/sign-in";
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream("""{"username":"nobody","password":"not a password","code":"000000"}"""u8.ToArray());
            http.Connection.RemoteIpAddress = IPAddress.Parse(from);
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            http.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        return (HttpStatusCode)context.Response.StatusCode;
    }

    private WebApplicationFactory<Program> Host(string? trustedNetworks) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Review:SignInAttemptsPerMinute", "2");
            if (trustedNetworks is not null)
            {
                builder.UseSetting("Gateway:Proxy:TrustedNetworks", trustedNetworks);
            }
        });
}
