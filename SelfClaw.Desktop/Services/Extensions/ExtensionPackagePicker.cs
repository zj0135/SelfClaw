using Microsoft.Win32;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Extensions.Abstractions;

namespace SelfClaw.Desktop.Services.Extensions;

internal sealed class ExtensionPackagePicker : IExtensionPackagePicker
{
    public string? PickPackage(ExtensionKind kind)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Title = kind == ExtensionKind.Skill ? "导入技能" : "导入插件",
            Filter = kind == ExtensionKind.Skill
                ? "技能包 (*.zip;*.selfclaw-skill;SKILL.md)|*.zip;*.selfclaw-skill;SKILL.md|所有文件 (*.*)|*.*"
                : "插件包 (*.zip;*.selfclaw-plugin)|*.zip;*.selfclaw-plugin|所有文件 (*.*)|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
