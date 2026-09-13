using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAiModelCatalog
{
    Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken cancellationToken = default);
}
