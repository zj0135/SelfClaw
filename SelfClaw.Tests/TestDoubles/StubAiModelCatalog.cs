using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class StubAiModelCatalog : IAiModelCatalog
{
    internal StubAiModelCatalog(Guid? defaultModelProfileId = null)
    {
        DefaultModelProfileId = defaultModelProfileId ?? Guid.NewGuid();
    }

    internal Guid? DefaultModelProfileId { get; set; }
    internal IReadOnlyList<EnabledModelView> EnabledModels { get; set; } = [];

    public Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultModelProfileId);

    public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(EnabledModels);

    public Task<bool> IsModelAvailableAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
        => Task.FromResult(EnabledModels.Any(model => model.ModelProfileId == modelProfileId));
}
