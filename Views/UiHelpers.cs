using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GuardWui3.Views;

// Small UI helpers shared by MainWindow and the dialogs (previously duplicated).
internal static class UiHelpers
{
    // A button Style deriving from an app style with an AccessKey setter:
    // ContentDialog's standard buttons expose no AccessKey property directly,
    // only their Style, so the mnemonic rides in one. A dialog's DEFAULT button
    // cannot use this route - the dialog's visual state overwrites its Style
    // with the accent style - so its key is set on the realized button via
    // FindDescendantByName instead (AccessKey is a separate property the Style
    // swap leaves alone).
    public static Style AccessKeyButtonStyle(string accessKey, string baseStyleKey = "DefaultButtonStyle")
    {
        var style = new Style(typeof(Button))
        {
            BasedOn = (Style)Application.Current.Resources[baseStyleKey]
        };
        style.Setters.Add(new Setter(UIElement.AccessKeyProperty, accessKey));
        return style;
    }

    // Depth-first search of a realized control's visual tree for a template part
    // by name (e.g. a ContentDialog's "PrimaryButton"), used to reach a button
    // the control does not surface as a settable property.
    public static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendantByName(child, name) is { } found) return found;
        }
        return null;
    }
}
