using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Persistence;

internal sealed record SubagentPersistenceTestContext(
    SqliteDatabase Database,
    SqliteConversationRepository Conversations,
    SqliteConversationTurnRepository Turns,
    SqliteSubagentTaskRepository Tasks,
    SqliteSubagentDeliveryRepository Deliveries);
