using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class PluginPermissionsTests
{
    // Permissions are a disclosure list, not a closed enum: nothing enforces an unrecognized token, and
    // rejecting one would break packages already shipping vocabulary such as workspace.read.
    [Fact]
    public void Validate_keeps_unknown_bare_permissions()
    {
        var permissions = PluginPermissions.Validate(["workspace.read", "process.execute", "some_future.thing"]);

        permissions.Should().Equal("process.execute", "some_future.thing", "workspace.read");
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com")]
    [InlineData("https://API.Example.com", "https://api.example.com")]
    [InlineData("https://api.example.com:443/", "https://api.example.com")]
    [InlineData("https://api.example.com:8443", "https://api.example.com:8443")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173/", "http://127.0.0.1:5173")]
    public void Validate_normalizes_network_origins_to_a_bare_origin(string declared, string expected)
    {
        var permissions = PluginPermissions.Validate([$"network.fetch:{declared}"]);

        permissions.Should().Equal($"network.fetch:{expected}");
    }

    [Theory]
    [InlineData("network.fetch:https://api.example.com/v1", "*bare origin*")]
    [InlineData("network.fetch:https://api.example.com?a=1", "*bare origin*")]
    [InlineData("network.fetch:https://user:pass@api.example.com", "*bare origin*")]
    [InlineData("network.fetch:not-a-url", "*bare origin*")]
    [InlineData("network.fetch:", "*bare origin*")]
    [InlineData("network.fetch:http://example.com", "*must use HTTPS*")]
    [InlineData("storage.read:everything", "*unsupported prefix*")]
    public void Validate_rejects_malformed_prefixed_permissions(string permission, string error)
    {
        var action = () => PluginPermissions.Validate([permission]);

        action.Should().Throw<InvalidDataException>().WithMessage(error);
    }

    // Two spellings of one origin must not acknowledge as two permissions, otherwise the CSP grant and
    // the dialog the user approved would drift apart.
    [Fact]
    public void Validate_rejects_origins_that_collide_after_normalization()
    {
        var action = () => PluginPermissions.Validate(
            ["network.fetch:https://api.example.com", "network.fetch:https://API.example.com:443/"]);

        action.Should().Throw<InvalidDataException>().WithMessage("*must be unique*");
    }

    [Fact]
    public void ReadNetworkOrigins_returns_only_the_declared_fetch_origins()
    {
        var permissions = PluginPermissions.Validate(
            ["ui.panel", "network.fetch:https://b.example.com", "network.fetch:https://a.example.com"]);

        PluginPermissions.ReadNetworkOrigins(permissions)
            .Should().Equal("https://a.example.com", "https://b.example.com");
    }

    [Fact]
    public void ReadNetworkOrigins_is_empty_when_nothing_is_declared()
    {
        PluginPermissions.ReadNetworkOrigins(PluginPermissions.Validate(["ui.panel"])).Should().BeEmpty();
    }
}
