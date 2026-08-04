using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.Pet;
using SelfClaw.Tests.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Services.Pet;

public sealed class PetSettingsBridgeTests
{
    [Fact]
    public async Task TryHandleAsync_returns_correlated_pet_state()
    {
        using var root = new TemporaryPetRoot();
        var catalog = new PetPackageCatalog(
            root.Path,
            new FakePetSpriteDecoder(_ => throw new InvalidOperationException("Sprite decoding is not expected.")),
            NullLogger<PetPackageCatalog>.Instance);
        var host = new PetHost(
            new InMemoryPetSettingsRepository(new PetSettings()),
            new FakePetWindowAdapter(),
            catalog,
            NullLogger<PetHost>.Instance);
        var bridge = new PetSettingsBridge(host);
        using var request = JsonDocument.Parse("""
            { "type": "get-pet-settings", "requestId": "pet-1" }
            """);

        var response = await bridge.TryHandleAsync("get-pet-settings", request.RootElement);

        response.Should().NotBeNull();
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(response));
        result.RootElement.GetProperty("requestId").GetString().Should().Be("pet-1");
        result.RootElement.GetProperty("type").GetString().Should().Be("pet-settings");
    }
}
