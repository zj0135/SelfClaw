using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Desktop.Pet;

internal sealed class DesktopPetSettingsRepository : IPetSettingsRepository
{
    private const string SettingsNodeName = "pet";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DesktopSettingsJsonStore _settingsStore;

    public DesktopPetSettingsRepository(DesktopSettingsJsonStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<PetSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await _settingsStore
            .ReadNodeAsync<PetSettings>(SettingsNodeName, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? new PetSettings();
    }

    public Task SaveAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _settingsStore.WriteNodeAsync(SettingsNodeName, settings, JsonOptions, cancellationToken);
    }
}
