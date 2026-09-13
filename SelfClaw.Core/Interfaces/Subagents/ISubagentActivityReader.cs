using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentActivityReader
{
    Task<SubagentActivityPage> ListAsync(SubagentActivityQuery query, CancellationToken cancellationToken = default);

    Task<SubagentActivityDetail?> GetDetailAsync(Guid parentConversationId, Guid taskId, CancellationToken cancellationToken = default);

}
