using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SelfClaw.Desktop.Controls;

public partial class LeftSidebar : UserControl
{
    private bool _areExtensionsExpanded;

    public event EventHandler? SystemSettingsRequested;
    public event EventHandler? NewConversationRequested;
    public event EventHandler? ProjectsToggleRequested;
    public event EventHandler? StandaloneConversationRequested;
    public event EventHandler? StandaloneConversationsToggleRequested;
    public event EventHandler<Guid>? WorkspaceRootSelected;
    public event EventHandler<Guid>? ProjectConversationRequested;
    public event EventHandler<Guid>? ConversationSelected;
    public event EventHandler<Guid>? ConversationDeleteRequested;

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

    private void OnSystemSettingsNodeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SystemSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnNewConversationNodeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        NewConversationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectsHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ProjectsToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectsImportButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnProjectsAddButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnStandaloneNewConversationButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        StandaloneConversationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnStandaloneHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StandaloneConversationsToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid workspaceRootId })
        {
            WorkspaceRootSelected?.Invoke(this, workspaceRootId);
        }
    }

    private void OnProjectNewConversationButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: Guid workspaceRootId })
        {
            ProjectConversationRequested?.Invoke(this, workspaceRootId);
        }
    }

    private void OnProjectActionsButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnConversationButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid conversationId })
        {
            ConversationSelected?.Invoke(this, conversationId);
        }
    }

    private void OnDeleteConversationButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: Guid conversationId })
        {
            ConversationDeleteRequested?.Invoke(this, conversationId);
        }
    }
}
