using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Network;

namespace SelfClaw.Tests.Infrastructure.Network;

public sealed class NetworkProxyEnvironmentTests
{
    [Fact]
    public void Direct_clears_an_inherited_proxy_and_marks_every_host_direct()
    {
        var entries = NetworkProxyEnvironment.Entries(new NetworkProxySettings(NetworkProxyMode.Direct));

        entries["HTTP_PROXY"].Should().BeEmpty();
        entries["HTTPS_PROXY"].Should().BeEmpty();
        entries["ALL_PROXY"].Should().BeEmpty();
        entries["NO_PROXY"].Should().Be("*");
    }

    [Fact]
    public void Custom_writes_one_http_address_for_every_proxy_variable()
    {
        var entries = NetworkProxyEnvironment.Entries(
            new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890),
            () => throw new InvalidOperationException("a filled-in custom endpoint must not read the registry"));

        entries.Should().HaveCount(3);
        entries.Values.Should().OnlyContain(value => value == "http://127.0.0.1:7890");
        entries.Should().NotContainKey("NO_PROXY");
    }

    [Fact]
    public void System_injects_the_address_the_windows_settings_point_at()
    {
        var entries = NetworkProxyEnvironment.Entries(
            new NetworkProxySettings(NetworkProxyMode.System),
            () => "127.0.0.1:7890");

        entries.Should().HaveCount(3);
        entries.Values.Should().OnlyContain(value => value == "http://127.0.0.1:7890");
    }

    [Fact]
    public void System_without_a_static_address_injects_nothing()
    {
        // A PAC-only configuration has no host:port to hand over, so the child keeps its own exit.
        NetworkProxyEnvironment.Entries(new NetworkProxySettings(NetworkProxyMode.System), () => null)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("", 7890)]
    [InlineData("   ", 7890)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public void An_incomplete_custom_endpoint_falls_back_to_the_system_address(string host, int port)
    {
        var entries = NetworkProxyEnvironment.Entries(
            new NetworkProxySettings(NetworkProxyMode.Custom, host, port),
            () => "10.0.0.1:3128");

        entries.Values.Should().OnlyContain(value => value == "http://10.0.0.1:3128");
    }

    [Fact]
    public void Apply_writes_the_entries_into_the_child_environment()
    {
        var startInfo = new ProcessStartInfo("cmd.exe");

        NetworkProxyEnvironment.Apply(
            new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890),
            startInfo);

        startInfo.Environment["HTTPS_PROXY"].Should().Be("http://127.0.0.1:7890");
        startInfo.Environment.Should().NotContainKey("NO_PROXY");
    }

    [Fact]
    public void Custom_with_ipv6_host_formats_authority_with_brackets()
    {
        var entries = NetworkProxyEnvironment.Entries(
            new NetworkProxySettings(NetworkProxyMode.Custom, "::1", 7890));

        entries.Values.Should().OnlyContain(value => value.StartsWith("http://[") && value.EndsWith("]:7890"));
    }

    [Fact]
    public void Apply_removes_an_inherited_no_proxy_in_custom_mode()
    {
        var startInfo = new ProcessStartInfo("cmd.exe");
        startInfo.Environment["NO_PROXY"] = "*";
        startInfo.Environment["no_proxy"] = "localhost";

        NetworkProxyEnvironment.Apply(
            new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890),
            startInfo);

        startInfo.Environment.Should().NotContainKey("NO_PROXY");
        startInfo.Environment.Should().NotContainKey("no_proxy");
    }
}
