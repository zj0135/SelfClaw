using System.ComponentModel;

namespace SelfClaw.Desktop.Services.Plugins;

/// <summary>
/// Implemented by the main view model. The context is captured in one place instead of being assembled
/// from whatever each caller happens to hold — that divergence is exactly what let the pushed shape
/// drift away from the pulled one. <see cref="INotifyPropertyChanged"/> is part of the contract because
/// workspace selection can move without any transcript publish, and the publisher has to hear about it.
/// </summary>
internal interface IPluginPanelContextSource : INotifyPropertyChanged
{
    PluginPanelContext CaptureContext();
}
