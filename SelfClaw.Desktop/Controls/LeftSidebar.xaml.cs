using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SelfClaw.Desktop.Controls;

public partial class LeftSidebar : UserControl
{
    private bool _areExtensionsExpanded;

    public LeftSidebar()
    {
        InitializeComponent();
    }

    private void OnExtensionsNodeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _areExtensionsExpanded = !_areExtensionsExpanded;
        ExtensionsChildren.Visibility = _areExtensionsExpanded
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        ExtensionsChevron.RenderTransform = new RotateTransform(_areExtensionsExpanded ? 90 : 0);
    }
}
