using System;
using GuardWui3.Models;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

public sealed partial class FolderDialog : ContentDialog
{
    public nint WindowHandle { get; set; }

    public string SourcePath => (TxtSource.Text ?? "").Trim();
    public string SubFolder => (TxtSub.Text ?? "").Trim();

    public FolderDialog()
    {
        InitializeComponent();
    }

    // Edit mode for an existing pair: same fields and OK button as Add, but
    // pre-populated and retitled so the operation is clear.
    public void LoadFolder(FolderPair pair)
    {
        Title = "Edit Folder";
        TxtSource.Text = pair.Source;
        TxtSub.Text = pair.SubFolder;
    }

    private async void OnBrowse(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Windows.Storage.StorageFolder? folder;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeFilter.Add("*");
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            // Mirrors OnPrimary's own inline dialog: the WinRT picker can throw
            // in an unpackaged app, and that must fail the browse, not the app.
            var msg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Title,
                Content = "Could not open the folder picker:\n\n" + ex.Message,
                CloseButtonText = "OK"
            };
            await msg.ShowAsync();
            return;
        }
        if (folder != null) TxtSource.Text = folder.Path;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? problem = Validate();
        if (problem == null) return;
        // Keep the dialog open and explain what is missing or invalid.
        var deferral = args.GetDeferral();
        args.Cancel = true;
        var msg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Title, // matches Add or Edit mode
            Content = problem,
            CloseButtonText = "OK"
        };
        await msg.ShowAsync();
        deferral.Complete();
    }

    // Guards mirror ExcludeDialog's: a quote breaks the generated script's
    // quoted robocopy arguments and a pipe the ini's Folders format (its values
    // are pipe-delimited, so one would silently truncate the entry on the next
    // load). The subfolder also becomes path segments under the destination, so
    // it must hold no other invalid-filename characters (backslash stays
    // allowed for nesting) and no ".." segment that would climb out of the
    // destination root. A % is rejected in the subfolder only: cmd expands
    // %...% at parse time in the generated script, and unlike a source (where
    // %USERPROFILE% is a feature) an environment variable makes no sense in a
    // new folder name. A literal % in a SOURCE stays legal and gets a save-time
    // warning instead (SaveValidation.UnresolvedPercentPaths).
    private string? Validate()
    {
        if (SourcePath.Length == 0 || SubFolder.Length == 0)
            return "Fill in both the source folder and the subfolder.";
        if (SourcePath.Contains('"') || SourcePath.Contains('|'))
            return "The source folder cannot contain quote (\") or pipe (|) characters.";
        foreach (char c in SubFolder)
            if (c is '"' or '|' or '<' or '>' or ':' or '?' or '*' or '/' or '%')
                return "The subfolder name cannot contain the characters \" | < > : ? * / or %.";
        foreach (var seg in SubFolder.Split('\\'))
            if (seg.Trim() == "..")
                return "The subfolder name cannot contain a \"..\" segment.";
        return null;
    }
}
