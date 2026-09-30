namespace SelfClaw.Desktop.Services.Plugins;

internal sealed class OpenPluginViewSession : IDisposable
{
    private readonly IDisposable _lease;
    public OpenPluginViewSession(string pluginId, string hostName, string rootPath, IDisposable lease,
        string contentSecurityPolicy, IReadOnlyList<string> permissions)
    {
        PluginId = pluginId;
        HostName = hostName;
        RootPath = rootPath;
        _lease = lease;
        ContentSecurityPolicy = contentSecurityPolicy;
        Permissions = permissions;
    }
    public string PluginId { get; }
    public string HostName { get; }
    public string RootPath { get; }
    public string ContentSecurityPolicy { get; }
    public IReadOnlyList<string> Permissions { get; }
    public HashSet<string> ViewKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void Dispose() => _lease.Dispose();
}
