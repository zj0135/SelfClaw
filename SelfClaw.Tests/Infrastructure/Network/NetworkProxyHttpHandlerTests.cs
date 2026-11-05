using System.Net;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Network;

namespace SelfClaw.Tests.Infrastructure.Network;

public sealed class NetworkProxyHttpHandlerTests
{
    [Fact]
    public void Direct_disables_the_proxy_entirely()
    {
        using var handler = Create(new NetworkProxySettings(NetworkProxyMode.Direct));

        handler.UseProxy.Should().BeFalse();
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void Custom_uses_the_configured_endpoint()
    {
        using var handler = Create(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeOfType<WebProxy>()
            .Which.Address.Should().Be(new Uri("http://127.0.0.1:7890"));
    }

    [Fact]
    public void System_leaves_the_exit_to_dotnet()
    {
        using var handler = Create(new NetworkProxySettings());

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeNull();
    }

    [Theory]
    [InlineData("", 7890)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public void An_incomplete_custom_endpoint_behaves_like_system(string host, int port)
    {
        using var handler = Create(new NetworkProxySettings(NetworkProxyMode.Custom, host, port));

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeNull();
    }

    private static SocketsHttpHandler Create(NetworkProxySettings settings)
        => (SocketsHttpHandler)NetworkProxyHttpHandler.Create(settings);
}
