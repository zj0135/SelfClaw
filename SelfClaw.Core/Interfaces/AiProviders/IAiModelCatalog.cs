using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAiModelCatalog
{
    Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken cancellationToken = default);

    Task<bool> IsModelAvailableAsync(Guid modelProfileId, CancellationToken cancellationToken = default);

    /// <summary>Whether the model accepts image input. Unknown or disabled models answer false.</summary>
    Task<bool> IsModelMultimodalAsync(Guid modelProfileId, CancellationToken cancellationToken = default);
}
