namespace SelfClaw.Core.Models;

public enum AutomationTaskKind
{
    None = 0,
    Once = 1,
    Daily = 2,
    Weekly = 3,
    Monthly = 4,
    Cron = 5
}
