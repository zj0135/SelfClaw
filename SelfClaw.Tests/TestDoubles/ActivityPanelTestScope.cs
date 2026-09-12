using System.ComponentModel;
using SelfClaw.Desktop.Services.Activities;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ActivityPanelTestScope(Guid? parent) : IActivityPanelScopeSource
{
    private Guid? _parent = parent;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid? CaptureActivityParent() => _parent;
    internal void Select(Guid? parent)
    {
        _parent = parent;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedConversation"));
    }
}
