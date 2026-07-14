using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Builder for one custom exclusion: the radio choice shapes the input so the
// user needn't know robocopy's /XD-vs-/XF split or wildcard syntax (a typed
// extension becomes *.ext automatically).
public sealed partial class ExcludeDialog : ContentDialog
{
    // Kept in step with SettingsStore.IsValidExcludePattern, which re-applies
    // this check to hand-edited ini values at load.
    private static readonly char[] UnsafePatternChars = { '"', '|', '&', '^', '<', '>', '%' };

    public bool IsFolder => RbFolder.IsChecked == true;
    public string Pattern { get; private set; } = "";

    public ExcludeDialog()
    {
        InitializeComponent();
        OnKindChanged(this, new RoutedEventArgs());
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        // The initial IsChecked fires during InitializeComponent, before the
        // later-declared controls exist.
        if (TxtValue == null || LblHint == null) return;
        string hint;
        if (RbFolder.IsChecked == true)
        {
            TxtValue.Header = "Folder name:";
            hint = "Every folder with this name is skipped wherever it appears "
                + "(for example node_modules or .git). Wildcards are allowed: "
                + "* matches any text, ? matches one character.";
        }
        else if (RbType.IsChecked == true)
        {
            TxtValue.Header = "File extension:";
            hint = "All files of this type are skipped, in every folder. "
                + "Type just the extension (for example iso or mp4); GUARD turns "
                + "it into a *.extension pattern for you.";
        }
        else
        {
            TxtValue.Header = "File name or pattern:";
            hint = "Files matching this name or pattern are skipped "
                + "(for example Thumbs.db or report-*.pdf). Wildcards: "
                + "* matches any text, ? matches one character.";
        }
        // Visible caption for sighted users; same text set as UIA HelpText so a
        // screen reader speaks it on focus.
        LblHint.Text = hint;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(TxtValue, hint);
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string t = (TxtValue.Text ?? "").Trim();
        string? problem = null;
        if (RbType.IsChecked == true)
        {
            // Accept "iso", ".iso" and "*.iso" alike.
            t = t.TrimStart('*').TrimStart('.').Trim();
            if (t.Length == 0) problem = "Type the file extension to exclude (for example iso).";
            else t = "*." + t;
        }
        else if (t.Length == 0)
        {
            problem = RbFolder.IsChecked == true
                ? "Type the folder name to exclude (for example node_modules)."
                : "Type the file name or pattern to exclude (for example *.tmp).";
        }
        // Quotes would break the generated robocopy line; pipes the ini format.
        // The cmd operator/expansion characters (& ^ < > %) cannot be made safe
        // either: the pattern lands in the script's set "OPTS=..." line, whose
        // outer quotes invert the polarity of any inner quoting, so when %OPTS%
        // expands on the robocopy line these characters act as operators.
        if (problem == null && t.IndexOfAny(UnsafePatternChars) >= 0)
            problem = "The name cannot contain any of these characters: \" | & ^ < > %";

        if (problem != null)
        {
            // Keep the dialog open and explain what is missing.
            var deferral = args.GetDeferral();
            args.Cancel = true;
            await UiHelpers.ShowNestedMessageAsync(this, problem);
            deferral.Complete();
            return;
        }
        Pattern = t;
    }
}
