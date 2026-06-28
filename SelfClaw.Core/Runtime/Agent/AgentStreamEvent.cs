namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Base type for every event emitted by an agent run, regardless of whether the
/// run is backed by a local CLI subprocess or a future in-process agent framework.
/// The ViewModel layer translates these into <c>MessageRecord</c> / <c>ToolExecutionRecord</c>
/// for the Vue transcript, so the rendering contract stays decoupled from the runtime.
/// </summary>
public abstract record AgentStreamEvent;
