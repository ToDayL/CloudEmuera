using CloudEmuera.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

// SEC-011: optional realtime Origin allowlist behavior and strict configuration validation.
public sealed class RealtimeOriginPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [Trait("Category", "Authorization")]
    public void UnconfiguredPolicyAllowsMissingOrArbitraryOrigin(string? configuredOrigins)
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse(configuredOrigins);
        DefaultHttpContext context = new();

        Assert.False(policy.IsConfigured);
        Assert.True(policy.IsAllowed(context));
        context.Request.Headers[HeaderNames.Origin] = "https://untrusted.example";
        Assert.True(policy.IsAllowed(context));
    }

    [Theory]
    [InlineData("HTTPS://GAME.EXAMPLE:443/")]
    [InlineData("http://localhost:5173")]
    [Trait("Category", "Authorization")]
    public void ConfiguredPolicyAllowsExactOriginsWithNormalizedSchemeHostAndDefaultPort(string origin)
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse("https://game.example, http://localhost:5173");
        DefaultHttpContext context = new();
        context.Request.Headers[HeaderNames.Origin] = origin;

        Assert.True(policy.IsAllowed(context));
    }

    [Fact]
    [Trait("Category", "Authorization")]
    public void ConfiguredPolicyDeduplicatesEquivalentOrigins()
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse("https://game.example,https://GAME.EXAMPLE:443/");
        DefaultHttpContext context = new();
        context.Request.Headers[HeaderNames.Origin] = "https://game.example";

        Assert.True(policy.IsAllowed(context));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://sub.game.example")]
    [InlineData("http://game.example")]
    [InlineData("https://game.example:444")]
    [InlineData("https://game.example/path")]
    [InlineData("https://game.example/./")]
    [Trait("Category", "Authorization")]
    public void ConfiguredPolicyRejectsMissingOrNonmatchingOrigin(string? origin)
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse("https://game.example");
        DefaultHttpContext context = new();
        if (origin is not null)
            context.Request.Headers[HeaderNames.Origin] = origin;

        Assert.False(policy.IsAllowed(context));
    }

    [Fact]
    [Trait("Category", "Authorization")]
    public void ConfiguredPolicyRejectsMultipleOriginHeaderValues()
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse("https://game.example");
        DefaultHttpContext context = new();
        context.Request.Headers.Append(HeaderNames.Origin, "https://game.example");
        context.Request.Headers.Append(HeaderNames.Origin, "https://other.example");

        Assert.False(policy.IsAllowed(context));
    }

    [Theory]
    [InlineData("https://game.example/path")]
    [InlineData("https://game.example?query=1")]
    [InlineData("https://game.example#fragment")]
    [InlineData("ftp://game.example")]
    [InlineData("https://user@game.example")]
    [InlineData("https://*.game.example")]
    [InlineData("https://game.example,")]
    [Trait("Category", "Authorization")]
    public void InvalidConfiguredOriginFailsPolicyConstruction(string configuredOrigins)
    {
        Assert.Throws<InvalidOperationException>(() => RealtimeOriginPolicy.Parse(configuredOrigins));
    }
}
