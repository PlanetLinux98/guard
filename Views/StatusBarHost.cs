using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Wraps status bar content so UIA exposes a real StatusBar element. Panels
// (Border/Grid) get no automation peer, so without this the bar is loose text
// that a screen reader's read-status-bar command (NVDA+End) cannot find.
public sealed partial class StatusBarHost : ContentControl
{
    public StatusBarHost()
    {
        IsTabStop = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusBarHostPeer(this);

    private sealed partial class StatusBarHostPeer : FrameworkElementAutomationPeer
    {
        public StatusBarHostPeer(StatusBarHost owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;
        protected override string GetClassNameCore() => "StatusBar";
    }
}
