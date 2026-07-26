using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Extensions.Abstractions;

public interface IExtensionPackagePicker
{
    string? PickPackage(ExtensionKind kind);
}
