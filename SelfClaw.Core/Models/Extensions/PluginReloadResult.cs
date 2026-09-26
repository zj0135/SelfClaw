namespace SelfClaw.Core.Models;

public sealed record PluginReloadResult(ExtensionPackageView Package, bool Changed);
