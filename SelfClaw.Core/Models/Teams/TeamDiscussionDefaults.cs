namespace SelfClaw.Core.Models;

public static class TeamDiscussionDefaults
{
    public const int MinRounds = 1;
    public const int DefaultMaxRounds = 2;
    public const int MaxRounds = 5;
    public const TeamOutputMode DefaultOutputMode = TeamOutputMode.AutoDocument;

    public static int ClampRounds(int value)
        => Math.Clamp(value, MinRounds, MaxRounds);
}
