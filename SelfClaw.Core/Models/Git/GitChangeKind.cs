namespace SelfClaw.Core.Models;

public enum GitChangeKind
{
    Modified = 0,
    Added = 1,
    Deleted = 2,
    Renamed = 3,
    Copied = 4,
    Untracked = 5,
    Conflicted = 6
}
