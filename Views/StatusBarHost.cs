using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Wraps the window status bar content so UIA exposes a real StatusBar element.
// Panels (Border/Grid) never get automation peers, so without this wrapper the
// bar is just loose text in the tree and a screen reader's read-status-bar
// command (NVDA+End) cannot find it.
public sealed partial class StatusBarHost : ContentControl
{
    public StatusBarHost()
    {
        IsTabStop = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusBarHostPeer(this);

    private sealed class StatusBarHostPeer : FrameworkElementAutomationPeer
    {
        public StatusBarHostPeer(StatusBarHost owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;
        protected override string GetClassNameCore() => "StatusBar";
    }
}
