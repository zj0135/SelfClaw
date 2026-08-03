using System.Text.Json;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Desktop.Services.Pet;

public sealed class PetSettingsBridge
{
    private const string AssetsHostName = "appassets.selfclaw.local";
    private readonly PetHost _petHost;

    public PetSettingsBridge(PetHost petHost)
    {
        _petHost = petHost;
    }

    public event Action<object>? ResponseReady;

    public async Task<bool> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("get-pet-settings" or "set-pet-visible" or "select-builtin-pet"))
        {
            return false;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var state = type switch
            {
                "get-pet-settings" => await _petHost.GetStateAsync(cancellationToken),
                "set-pet-visible" => await _petHost.ExecuteAsync(
                    new PetHostCommand(
                        ReadBoolean(payload, "enabled")
                            ? PetHostCommandKind.Show
                            : PetHostCommandKind.Hide),
                    cancellationToken),
                "select-builtin-pet" => await _petHost.ExecuteAsync(
                    new PetHostCommand(
                        PetHostCommandKind.SelectBuiltInPet,
                        ReadOptionalString(payload, "petId")),
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pet settings message type '{type}'.")
            };
            PostState(requestId, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Post(new
            {
                type = "pet-settings",
                requestId,
                enabled = false,
                selectedPetId = (string?)null,
                spriteSheetPath = (string?)null,
                error = exception.Message
            });
        }

        return true;
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) &&
           element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           element.GetBoolean();

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void PostState(string? requestId, PetHostState state)
        => Post(new
        {
            type = "pet-settings",
            requestId,
            enabled = state.Settings.Enabled,
            selectedPetId = state.SelectedBuiltInPetId,
            spriteSheetPath = state.Settings.SpriteSheetPath,
            pets = state.BuiltInPackages.Select(package => new
            {
                package.Id,
                package.DisplayName,
                package.Description,
                package.Author,
                package.Tags,
                package.Source,
                package.SourceUrl,
                previewSrc = $"https://{AssetsHostName}/{package.PreviewAssetPath}",
                cols = package.Columns,
                rows = package.Rows
            }).ToArray()
        });

    private void Post(object payload) => ResponseReady?.Invoke(payload);
}
