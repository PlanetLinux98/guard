using System;
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

    private async void OnBrowse(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) TxtSource.Text = folder.Path;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SourcePath.Length == 0 || SubFolder.Length == 0)
        {
            // Keep the dialog open and explain what is missing.
            var deferral = args.GetDeferral();
            args.Cancel = true;
            var msg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Add Folder",
                Content = "Fill in both the source folder and the subfolder.",
                CloseButtonText = "OK"
            };
            await msg.ShowAsync();
            deferral.Complete();
        }
    }
}
