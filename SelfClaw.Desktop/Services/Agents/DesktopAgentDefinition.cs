using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopAgentDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyList<string> McpServers,
    IReadOnlyList<string> DisabledMcpServers,
    string Instructions,
    string FilePath,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;

    public IReadOnlyList<string> EnabledSkills => FilterEnabledServices(Skills, DisabledSkills);

    public IReadOnlyList<string> EnabledMcpServers => FilterEnabledServices(McpServers, DisabledMcpServers);

    public AgentRuntimeDefinition ToRuntimeDefinition()
        => new(
            Id,
            Name,
            Description,
            Mode,
            ToolPolicy,
            EnabledSkills,
            EnabledMcpServers,
            Instructions);

    private static IReadOnlyList<string> FilterEnabledServices(
        IReadOnlyList<string> selectedServices,
        IReadOnlyList<string> disabledServices)
    {
        if (selectedServices.Count == 0)
        {
            return [];
        }

        if (disabledServices.Count == 0)
        {
            return selectedServices.ToArray();
        }

        var disabled = disabledServices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selectedServices
            .Where(item => !disabled.Contains(item))
            .ToArray();
    }
}
