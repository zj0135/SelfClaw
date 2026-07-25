namespace SelfClaw.Desktop.Services.AgentActivity;

public enum AgentActivityPhase
{
    Idle,
    Starting,
    Initializing,
    Requesting,
    Thinking,
    Responding,
    UsingTool,
    AwaitingApproval,
    Succeeded,
    Failed,
    Cancelled,
}
