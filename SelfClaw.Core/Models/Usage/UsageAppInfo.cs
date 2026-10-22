namespace SelfClaw.Core.Models;

/// <summary>
/// Product identity for the About page header. Filled by the Desktop layer — Infrastructure must not
/// look at WPF assembly attributes. <see cref="Build"/> is the short source revision, null when the
/// build carried no informational version.
/// </summary>
public sealed record UsageAppInfo(string Version, string? Build, string Runtime, string Os);
