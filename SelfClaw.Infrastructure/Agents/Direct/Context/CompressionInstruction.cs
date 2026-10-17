namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// Fixed instruction for the summarization call. It names what must survive compression because a
/// summary that drops file paths, open work or known errors makes the rest of the turn useless.
/// </summary>
internal static class CompressionInstruction
{
    internal const string Text =
        "You summarize the earlier part of this conversation so the summary can replace those messages.\n" +
        "Preserve: the user's goals and constraints; decisions already made; the file paths, commands and " +
        "identifiers that were used; what is still unfinished and any known errors; the key conclusions.\n" +
        "Discard: greetings, repetition, and intermediate attempts that were superseded or abandoned.\n" +
        "Do not introduce facts that are not present in the messages. Use concise written prose or short " +
        "lists. Reply with the summary only.";
}
