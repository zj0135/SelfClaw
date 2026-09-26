using System.Text.Json.Serialization;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

/// <summary>
/// Model-visible hook feedback attached to a tool result. <paramref name="Source"/> is either
/// <c>selfclaw</c> (the host's argument-rewrite note) or <c>&lt;plugin&gt;/&lt;hook&gt;</c>.
/// </summary>
internal sealed record DirectToolHookFeedback(string Source, string Text);
