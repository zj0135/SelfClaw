namespace SelfClaw.Core.Models;

/// <summary>
/// Optional refinements for a workspace text search. All members are optional;
/// unset values fall back to the service defaults.
/// </summary>
public sealed record WorkspaceSearchOptions
{
    /// <summary>
    /// Glob that limits which files are searched (e.g. <c>src/**/*.cs</c>, <c>*.md</c>).
    /// When null or empty, every searchable text file is considered.
    /// </summary>
    public string? Glob { get; init; }

    /// <summary>
    /// Workspace-relative directory to scope the search to. When null or empty,
    /// the search starts at the workspace root.
    /// </summary>
    public string? RelativePath { get; init; }

    /// <summary>
    /// Treat <c>query</c> as a regular expression instead of a literal substring.
    /// </summary>
    public bool IsRegex { get; init; }

    /// <summary>
    /// Match case-sensitively. Defaults to case-insensitive.
    /// </summary>
    public bool CaseSensitive { get; init; }

    /// <summary>
    /// Maximum number of matching lines to return. When null, the service default applies.
    /// </summary>
    public int? MaxResults { get; init; }
}
