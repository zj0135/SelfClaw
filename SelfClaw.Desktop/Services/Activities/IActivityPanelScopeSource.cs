using System.ComponentModel;

namespace SelfClaw.Desktop.Services.Activities;

internal interface IActivityPanelScopeSource : INotifyPropertyChanged
{
    Guid? CaptureActivityParent();
}
