using FluentAssertions;
using SelfClaw.Infrastructure.Network;

namespace SelfClaw.Tests.Infrastructure.Network;

public sealed class SystemProxyAddressReaderTests
{
    [Theory]
    [InlineData("127.0.0.1:7890", "127.0.0.1:7890")]
    [InlineData("  127.0.0.1:7890  ", "127.0.0.1:7890")]
    [InlineData("proxy.example.test:8080", "proxy.example.test:8080")]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8443", "127.0.0.1:8443")]
    [InlineData("https=127.0.0.1:8443;http=127.0.0.1:8080", "127.0.0.1:8443")]
    [InlineData("ftp=127.0.0.1:21;http=127.0.0.1:8080", "127.0.0.1:8080")]
    [InlineData("http=127.0.0.1:8080;ftp=127.0.0.1:21", "127.0.0.1:8080")]
    [InlineData("socks=127.0.0.1:1080", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("not-a-host-port", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("127.0.0.1:0", null)]
    [InlineData("127.0.0.1:65536", null)]
    [InlineData("127.0.0.1:abc", null)]
    [InlineData("fe80::1", null)]
    [InlineData("[::1]:7890", "[::1]:7890")]
    [InlineData("http=;https=", null)]
    public void Parse_accepts_only_an_address_a_proxy_variable_can_carry(string? server, string? expected)
        => SystemProxyAddressReader.Parse(server).Should().Be(expected);
}
