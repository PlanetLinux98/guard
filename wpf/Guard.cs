// SPDX-License-Identifier: MIT
// Copyright (c) 2026 PlanetLinux98
//
// Guard.cs  (WPF edition)
// GUARD: a portable WPF manager for backing up a Windows PC to any folder
// destination (a local drive, an external disk, or a network share).
//
// "Generator" model: settings live in backup-settings.ini next to this
// program; Save Settings rewrites guard-backup.cmd from those settings and
// creates/updates the matching daily scheduled task. The .cmd stays fully
// standalone (runs the same from the app, Task Scheduler, or a double-click).
//
// Two tabs:
//   * File Backup   - the Robocopy copy job (with per-folder progress).
//   * App Inventory - lists installed apps (from the registry, enriched by
//                     winget when it is present), lets you tick which to keep,
//                     export the list to your chosen destination, and after an
//                     OS reinstall import it and reinstall the winget-capable
//                     ones automatically.
//
// Why WPF: native, fully-themeable dark/light mode. Unlike WinForms, WPF lets
// us restyle EVERY control - scrollbars, tab strip, group borders, combo
// dropdowns, checkboxes - via Styles and ControlTemplates in a swappable
// ResourceDictionary. The dictionaries are authored as XAML strings parsed at
// runtime with XamlReader, so this remains a single .cs file with NO XAML
// file and NO project file, compiled by the in-box .NET Framework csc.exe.
//
// Build (one line, in-box .NET Framework 4.x compiler, no SDK required).
// Run from this folder. NOTE: on .NET Framework the registry API lives in
// mscorlib, so unlike the spec's draft command there is NO separate
// Microsoft.Win32.Registry.dll reference (that assembly only exists on .NET
// Core / 5+ and the in-box csc cannot find it). System.Windows.Forms is
// referenced solely for FolderBrowserDialog (WPF has no folder picker).
//
//   "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe ^
//     /r:WPF\PresentationFramework.dll /r:WPF\PresentationCore.dll ^
//     /r:WPF\WindowsBase.dll /r:System.Xaml.dll /r:System.dll /r:System.Core.dll ^
//     /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll ^
//     /out:GUARD.exe Guard.cs
//
// (The WPF\* references resolve relative to csc.exe's own folder, which
//  contains a WPF subdirectory - so no local copy of those DLLs is needed.)
//
// Accessibility: every input has an AutomationProperties.Name (WPF exposes it
// to NVDA via UI Automation), labels carry _access keys with Target focus
// transfer, and Tab order follows reading order.
//
// NOTE: in-box csc targets C# 5 - no interpolated strings, no pattern
// matching, no ?. operator, no nameof. Written to that syntax level.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Guard
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Theme.Install(app);
            try
            {
                var win = new MainWindow();
                app.Run(win);
            }
            catch (Exception ex)
            {
                MessageBox.Show("GUARD hit an unexpected error and must close:\r\n\r\n"
                    + ex, "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ----- one backup folder pair -------------------------------------------
    // Properties (not fields) so WPF data binding can two-way bind the include
    // checkbox in the folder list. Logic call-sites (f.Include / f.Source /
    // f.SubFolder) are identical to field access, so the ports are unchanged.
    class FolderPair : INotifyPropertyChanged
    {
        bool _include;
        public bool Include
        {
            get { return _include; }
            set { _include = value; OnChanged("Include"); }
        }
        public string Source { get; set; }     // may contain %USERPROFILE% etc.
        public string SubFolder { get; set; }   // name under the destination root
        public FolderPair(bool inc, string src, string sub) { _include = inc; Source = src; SubFolder = sub; }

        // Spoken name for the row's checkbox. The checked/unchecked state is
        // announced by the checkbox role itself, so it is NOT included here.
        public string Caption { get { return "Source folder, " + Source + ", subfolder, " + SubFolder; } }

        public override string ToString() { return Caption; }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    // ----- one installed-application row (App Inventory tab) ----------------
    // Source is "winget" (winget knows a package id, so it can be reinstalled
    // automatically) or "manual" (in Add/Remove Programs but not in any winget
    // source, so reinstall is by hand). Publisher / InstallLocation /
    // PublisherUrl are accurate values read from the registry; they are blank
    // when not present (we never fabricate a download link).
    class AppEntry : INotifyPropertyChanged
    {
        bool _include = true;
        public bool Include
        {
            get { return _include; }
            set { _include = value; OnChanged("Include"); }
        }
        public string Name { get; set; }
        public string Id { get; set; }              // winget package id (auto apps only)
        public string Version { get; set; }
        public string Source { get; set; }          // "winget" | "msstore" | "manual"
        public string Publisher { get; set; }
        public string InstallLocation { get; set; }
        public string PublisherUrl { get; set; }

        public bool CanAuto { get { return Source == "winget" || Source == "msstore"; } }

        // Shown in the "Source" column.
        public string SourceLabel
        {
            get
            {
                if (Source == "winget") return "Winget";
                if (Source == "msstore") return "Store";
                return "Manual";
            }
        }

        // Spoken name for the row's checkbox (checked state announced by the role).
        public string Caption
        {
            get
            {
                string v = string.IsNullOrEmpty(Version) ? "" : ", version " + Version;
                return "Application, " + Name + v + ", " + SourceLabel +
                    (CanAuto ? ", reinstallable" : ", manual install");
            }
        }

        public override string ToString() { return Caption; }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    // ----- app-list file schema (JSON, written to / read from the destination) -----
    [DataContract]
    class AppListItem
    {
        [DataMember(Name = "name", Order = 0)] public string Name;
        [DataMember(Name = "id", Order = 1)] public string Id;
        [DataMember(Name = "source", Order = 2)] public string Source;
        [DataMember(Name = "version", Order = 3)] public string Version;
        [DataMember(Name = "publisher", Order = 4)] public string Publisher;
        [DataMember(Name = "installLocation", Order = 5)] public string InstallLocation;
        [DataMember(Name = "publisherUrl", Order = 6)] public string PublisherUrl;
    }

    [DataContract]
    class AppListFile
    {
        [DataMember(Name = "exported", Order = 0)] public string Exported;
        [DataMember(Name = "machine", Order = 1)] public string Machine;
        [DataMember(Name = "apps", Order = 2)] public AppListItem[] Apps;
    }

    // ----- the whole configuration ------------------------------------------
    class Settings
    {
        public string Dest = "";                       // any folder: local drive, external disk, or network share
        public string Mode = "Additive";               // Additive | Mirror
        public string ExcludeDirs = "node_modules\r\n$RECYCLE.BIN\r\n.git";
        public string ExcludeFiles = "Thumbs.db\r\ndesktop.ini\r\n.DS_Store";
        public bool ScheduleEnabled = true;
        public string ScheduleTime = "02:00";
        public ObservableCollection<FolderPair> Folders = new ObservableCollection<FolderPair>();

        // App Inventory tab: where the exported app-list.json is written.
        public string AppListDest = "";

        public static ObservableCollection<FolderPair> DefaultFolders()
        {
            return new ObservableCollection<FolderPair>
            {
                new FolderPair(true, @"%USERPROFILE%\Documents",  "Documents"),
                new FolderPair(true, @"%USERPROFILE%\Videos",     "Videos"),
                new FolderPair(true, @"%USERPROFILE%\Desktop",    "Desktop"),
                new FolderPair(true, @"%USERPROFILE%\Pictures",   "Pictures"),
                new FolderPair(true, @"%USERPROFILE%\Music",      "Music"),
                new FolderPair(true, @"%USERPROFILE%\Favorites",  "Favorites"),
                new FolderPair(true, @"%USERPROFILE%\Contacts",   "Contacts"),
            };
        }
    }

    // =========================================================================
    //  THEME SUPPORT
    //  Two ResourceDictionaries hold the colour palette as named SolidColorBrush
    //  resources; a third (shared) dictionary holds implicit Styles +
    //  ControlTemplates that reference those brushes via {DynamicResource ...}.
    //  Swapping the brushes dictionary re-resolves every DynamicResource, so the
    //  whole UI - scrollbars, tabs, borders included - reskins instantly.
    //
    //  - OS preference read from the registry (AppsUseLightTheme == 0 -> dark).
    //  - Title bar darkened via DwmSetWindowAttribute (immersive dark mode).
    //  - Live theme changes handled in MainWindow via WM_SETTINGCHANGE.
    // =========================================================================
    static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    }

    static class Theme
    {
        const string Xmlns =
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

        static string DarkBrushes()
        {
            return "<ResourceDictionary " + Xmlns + ">" +
                "<SolidColorBrush x:Key='WindowBrush' Color='#FF202020'/>" +
                "<SolidColorBrush x:Key='ControlBrush' Color='#FF373737'/>" +
                "<SolidColorBrush x:Key='ControlHoverBrush' Color='#FF454545'/>" +
                "<SolidColorBrush x:Key='InputBrush' Color='#FF2D2D30'/>" +
                "<SolidColorBrush x:Key='TextBrush' Color='#FFDCDCDC'/>" +
                "<SolidColorBrush x:Key='DimTextBrush' Color='#FFA0A0A0'/>" +
                "<SolidColorBrush x:Key='BorderBrush' Color='#FF505050'/>" +
                "<SolidColorBrush x:Key='AccentBrush' Color='#FF4A90D9'/>" +
                "<SolidColorBrush x:Key='SelectionBrush' Color='#FF2D4A6B'/>" +
                "</ResourceDictionary>";
        }

        static string LightBrushes()
        {
            return "<ResourceDictionary " + Xmlns + ">" +
                "<SolidColorBrush x:Key='WindowBrush' Color='#FFF0F0F0'/>" +
                "<SolidColorBrush x:Key='ControlBrush' Color='#FFE1E1E1'/>" +
                "<SolidColorBrush x:Key='ControlHoverBrush' Color='#FFD6E8F8'/>" +
                "<SolidColorBrush x:Key='InputBrush' Color='#FFFFFFFF'/>" +
                "<SolidColorBrush x:Key='TextBrush' Color='#FF000000'/>" +
                "<SolidColorBrush x:Key='DimTextBrush' Color='#FF666666'/>" +
                "<SolidColorBrush x:Key='BorderBrush' Color='#FFA0A0A0'/>" +
                "<SolidColorBrush x:Key='AccentBrush' Color='#FF0078D7'/>" +
                "<SolidColorBrush x:Key='SelectionBrush' Color='#FFCCE4F7'/>" +
                "</ResourceDictionary>";
        }

        public static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    var val = key.GetValue("AppsUseLightTheme");
                    return val is int && (int)val == 0;
                }
            }
            catch { return false; }
        }

        static ResourceDictionary Parse(string xaml)
        {
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        // Slot 0 = brushes (swappable). Slot 1 = styles/templates (constant).
        public static void Install(Application app)
        {
            app.Resources.MergedDictionaries.Add(Parse(IsDark() ? DarkBrushes() : LightBrushes()));
            app.Resources.MergedDictionaries.Add(Parse(Styles()));
        }

        public static void Reapply(Application app)
        {
            if (app == null || app.Resources.MergedDictionaries.Count == 0) return;
            app.Resources.MergedDictionaries[0] = Parse(IsDark() ? DarkBrushes() : LightBrushes());
        }

        public static void ApplyTitleBar(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int v = IsDark() ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(hwnd,
                    NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
            }
            catch { }
        }

        // ---- implicit styles + control templates (theme-agnostic; colours via
        //      DynamicResource so they follow the swappable brush dictionary) ----
        static string Styles()
        {
            var s = new StringBuilder();
            s.Append("<ResourceDictionary " + Xmlns + ">");

            // Window-level text default
            s.Append("<Style TargetType='TextBlock'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='TextWrapping' Value='Wrap'/></Style>");

            // Label
            s.Append("<Style TargetType='Label'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Padding' Value='0,0,0,0'/>" +
                "<Setter Property='VerticalAlignment' Value='Center'/></Style>");

            // TextBox (default template honours Background/BorderBrush)
            s.Append("<Style TargetType='TextBox'>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/>" +
                "<Setter Property='Padding' Value='3,2'/>" +
                "<Setter Property='CaretBrush' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='SelectionBrush' Value='{DynamicResource AccentBrush}'/>" +
                "<Setter Property='AllowDrop' Value='True'/></Style>");

            // Button
            s.Append("<Style TargetType='Button'>" +
                "<Setter Property='Background' Value='{DynamicResource ControlBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/>" +
                "<Setter Property='Padding' Value='10,4'/>" +
                "<Setter Property='SnapsToDevicePixels' Value='True'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='Button'>" +
                "<Border x:Name='bd' CornerRadius='3' SnapsToDevicePixels='True'" +
                " Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}' RecognizesAccessKey='True'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/></Trigger>" +
                "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource SelectionBrush}'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource DimTextBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // CheckBox
            s.Append("<Style TargetType='CheckBox'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='VerticalContentAlignment' Value='Center'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='CheckBox'>" +
                "<StackPanel Orientation='Horizontal' Background='Transparent'>" +
                "<Border x:Name='box' Width='16' Height='16' CornerRadius='2' BorderThickness='1' VerticalAlignment='Center'" +
                " BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'>" +
                "<Path x:Name='chk' Stretch='Uniform' Margin='3' Stroke='#FFFFFFFF' StrokeThickness='2'" +
                " Data='M 0,5 L 4,9 L 10,0' Visibility='Collapsed'/>" +
                "</Border>" +
                "<ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>" +
                "</StackPanel>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='chk' Property='Visibility' Value='Visible'/>" +
                "<Setter TargetName='box' Property='Background' Value='{DynamicResource AccentBrush}'/>" +
                "<Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // RadioButton
            s.Append("<Style TargetType='RadioButton'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='VerticalContentAlignment' Value='Center'/>" +
                "<Setter Property='Margin' Value='0,3'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='RadioButton'>" +
                "<StackPanel Orientation='Horizontal' Background='Transparent'>" +
                "<Border x:Name='box' Width='16' Height='16' CornerRadius='8' BorderThickness='1' VerticalAlignment='Center'" +
                " BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'>" +
                "<Ellipse x:Name='dot' Width='8' Height='8' Fill='#FFFFFFFF' Visibility='Collapsed'/>" +
                "</Border>" +
                "<ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>" +
                "</StackPanel>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='dot' Property='Visibility' Value='Visible'/>" +
                "<Setter TargetName='box' Property='Background' Value='{DynamicResource AccentBrush}'/>" +
                "<Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // GroupBox
            s.Append("<Style TargetType='GroupBox'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/>" +
                "<Setter Property='Margin' Value='0,4'/>" +
                "<Setter Property='Padding' Value='10'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='GroupBox'>" +
                "<Border BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4' Margin='0,8,0,0'>" +
                "<Grid><Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition/></Grid.RowDefinitions>" +
                "<Border Padding='6,0,6,0' Margin='8,-9,0,0' HorizontalAlignment='Left' Background='{DynamicResource WindowBrush}'>" +
                "<ContentPresenter ContentSource='Header' TextElement.Foreground='{DynamicResource TextBrush}'/></Border>" +
                "<ContentPresenter Grid.Row='1' Margin='{TemplateBinding Padding}'/>" +
                "</Grid></Border></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // ComboBox + ComboBoxItem
            s.Append("<Style TargetType='ComboBox'>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/>" +
                "<Setter Property='Height' Value='26'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ComboBox'>" +
                "<Grid>" +
                "<ToggleButton Focusable='False' ClickMode='Press'" +
                " IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>" +
                "<Border Background='{DynamicResource InputBrush}' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='1' CornerRadius='3'>" +
                "<Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='20'/></Grid.ColumnDefinitions>" +
                "<Path Grid.Column='1' HorizontalAlignment='Center' VerticalAlignment='Center' Fill='{DynamicResource TextBrush}' Data='M 0,0 L 8,0 L 4,4 Z'/>" +
                "</Grid></Border></ControlTemplate></ToggleButton.Template></ToggleButton>" +
                "<ContentPresenter Margin='8,0,24,0' VerticalAlignment='Center' HorizontalAlignment='Left' IsHitTestVisible='False'" +
                " Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'" +
                " TextElement.Foreground='{DynamicResource TextBrush}'/>" +
                "<Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Slide'" +
                " IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<Border Background='{DynamicResource ControlBrush}' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='1'" +
                " MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>" +
                "<ScrollViewer><ItemsPresenter/></ScrollViewer></Border></Popup>" +
                "</Grid></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("<Style TargetType='ComboBoxItem'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Padding' Value='8,4'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ComboBoxItem'>" +
                "<Border x:Name='b' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource SelectionBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // TabControl + TabItem
            s.Append("<Style TargetType='TabControl'>" +
                "<Setter Property='Background' Value='{DynamicResource WindowBrush}'/>" +
                "<Setter Property='BorderThickness' Value='0'/>" +
                "<Setter Property='Padding' Value='0'/></Style>");

            s.Append("<Style TargetType='TabItem'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='TabItem'>" +
                "<Border x:Name='b' Margin='0,0,3,0' Padding='14,7' BorderThickness='1,1,1,0' CornerRadius='5,5,0,0'" +
                " Background='{DynamicResource ControlBrush}' BorderBrush='{DynamicResource BorderBrush}'>" +
                "<ContentPresenter ContentSource='Header' RecognizesAccessKey='True' HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsSelected' Value='True'>" +
                "<Setter TargetName='b' Property='Background' Value='{DynamicResource WindowBrush}'/>" +
                "<Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='False'>" +
                "<Setter Property='Foreground' Value='{DynamicResource DimTextBrush}'/></Trigger>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='b' Property='Background' Value='{DynamicResource ControlHoverBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // ListView / ListViewItem / GridViewColumnHeader
            s.Append("<Style TargetType='ListView'>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/></Style>");

            s.Append("<Style TargetType='ListViewItem'>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ListViewItem'>" +
                "<Border x:Name='b' Background='{TemplateBinding Background}' SnapsToDevicePixels='True'>" +
                "<GridViewRowPresenter VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource SelectionBrush}'/></Trigger>" +
                "<MultiTrigger><MultiTrigger.Conditions>" +
                "<Condition Property='IsMouseOver' Value='True'/><Condition Property='IsSelected' Value='False'/>" +
                "</MultiTrigger.Conditions><Setter TargetName='b' Property='Background' Value='{DynamicResource ControlHoverBrush}'/></MultiTrigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("<Style TargetType='GridViewColumnHeader'>" +
                "<Setter Property='Background' Value='{DynamicResource ControlBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='0,0,1,1'/>" +
                "<Setter Property='Padding' Value='6,4'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='GridViewColumnHeader'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'" +
                " BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter HorizontalAlignment='Left'/></Border></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // DataGrid (accessible table used for the folder + drive lists)
            s.Append("<Style TargetType='DataGrid'>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='1'/>" +
                "<Setter Property='RowBackground' Value='Transparent'/>" +
                "<Setter Property='HorizontalGridLinesBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='VerticalGridLinesBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='RowHeaderWidth' Value='0'/></Style>");

            s.Append("<Style TargetType='DataGridColumnHeader'>" +
                "<Setter Property='Background' Value='{DynamicResource ControlBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='BorderThickness' Value='0,0,1,1'/>" +
                "<Setter Property='Padding' Value='6,4'/>" +
                "<Setter Property='HorizontalContentAlignment' Value='Left'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='DataGridColumnHeader'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'" +
                " BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center'/></Border></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("<Style TargetType='DataGridRow'>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/></Style>");

            s.Append("<Style TargetType='DataGridCell'>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/>" +
                "<Setter Property='BorderBrush' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='DataGridCell'>" +
                "<Border x:Name='cb' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'" +
                " BorderThickness='{TemplateBinding BorderThickness}' Padding='4,3'>" +
                "<ContentPresenter VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsSelected' Value='True'>" +
                "<Setter TargetName='cb' Property='Background' Value='{DynamicResource SelectionBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource TextBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // ScrollBar (minimal: themed track + rounded thumb)
            s.Append("<Style x:Key='guardSbPage' TargetType='RepeatButton'>" +
                "<Setter Property='Focusable' Value='False'/><Setter Property='IsTabStop' Value='False'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("<Style x:Key='guardSbThumb' TargetType='Thumb'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='Thumb'><Border CornerRadius='4' Margin='3' Background='{DynamicResource BorderBrush}'/></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("<Style TargetType='ScrollBar'>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Width' Value='14'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='{DynamicResource WindowBrush}'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='True'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource guardSbPage}' Command='ScrollBar.PageUpCommand'/></Track.DecreaseRepeatButton>" +
                "<Track.Thumb><Thumb Style='{StaticResource guardSbThumb}'/></Track.Thumb>" +
                "<Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource guardSbPage}' Command='ScrollBar.PageDownCommand'/></Track.IncreaseRepeatButton>" +
                "</Track></Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='Orientation' Value='Horizontal'>" +
                "<Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='14'/>" +
                "<Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            // ProgressBar (determinate; AccentBrush fill over an InputBrush trough)
            s.Append("<Style TargetType='ProgressBar'>" +
                "<Setter Property='Height' Value='14'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource AccentBrush}'/>" +
                "<Setter Property='Background' Value='{DynamicResource InputBrush}'/>" +
                "<Setter Property='BorderBrush' Value='{DynamicResource BorderBrush}'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ProgressBar'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='3' SnapsToDevicePixels='True'>" +
                "<Grid x:Name='PART_Track' ClipToBounds='True' Margin='1'>" +
                "<Decorator x:Name='PART_Indicator' HorizontalAlignment='Left'>" +
                "<Border Background='{DynamicResource AccentBrush}' CornerRadius='2'/>" +
                "</Decorator></Grid></Border></ControlTemplate>" +
                "</Setter.Value></Setter></Style>");

            s.Append("</ResourceDictionary>");
            return s.ToString();
        }
    }

    // =========================================================================
    //  MAIN WINDOW
    // =========================================================================
    class MainWindow : Window
    {
        // Fixed locations: this program derives every path from its own exe
        // folder, so the whole folder is portable.
        static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string IniPath { get { return Path.Combine(BaseDir, "backup-settings.ini"); } }
        string ScriptPath { get { return Path.Combine(BaseDir, "guard-backup.cmd"); } }
        string LogPath { get { return Path.Combine(BaseDir, @"Logs\backup_last.log"); } }

        const string FileTaskName = "Daily GUARD Backup";
        const string AppListFileName = "app-list.json";

        public const string AppVersion = "0.1";
        public const string RepoUrl = "https://github.com/PlanetLinux98/guard";

        Settings cfg = new Settings();
        Process runningProc;

        // --- file-tab controls ---
        TextBox txtDest, txtExDirs, txtExFiles, txtOutput, txtTime;
        ItemsControl folderList;
        FolderPair currentFolder;   // last folder-row checkbox to hold focus (for Remove)
        RadioButton rbMirror, rbAdditive;
        CheckBox chkSchedule;
        Label lblNextRun;
        // Script-status indicator + unsaved-changes tracking.
        System.Windows.Shapes.Ellipse scriptDot;
        TextBlock scriptStatusText;
        bool dirty;
        // File-backup progress (driven by @@PROGRESS@@ markers in the script).
        ProgressBar fileProgress;
        Label fileProgressLabel;
        int progTotal;

        // --- app-inventory-tab controls ---
        TextBox txtAppDest, txtAppFilter, txtAppOutput;
        ItemsControl appList;
        ObservableCollection<AppEntry> appRows = new ObservableCollection<AppEntry>();
        ICollectionView appView;
        Label appStatus, appProgressLabel;
        ProgressBar appProgress;
        Button btnAppRefresh, btnAppExport, btnAppImport, btnAppReinstall, btnAppAll, btnAppNone;
        TabItem tabInventory;
        string appFilter = "";
        bool scanning, reinstalling, appScanned;
        bool wingetAvailable;       // set by the most recent scan; drives the status wording
        Process reinstallProc;

        public MainWindow()
        {
            Title = "GUARD";
            Width = 820;
            Height = 680;
            MinWidth = 700;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty, "WindowBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");

            LoadSettings();

            var tabs = new TabControl();
            AutomationProperties.SetName(tabs, "Backup sections");

            var tabFile = new TabItem { Header = "_File Backup" };
            tabFile.Content = BuildFileTab();
            tabInventory = new TabItem { Header = "App _Inventory" };
            tabInventory.Content = BuildInventoryTab();
            tabs.Items.Add(tabFile);
            tabs.Items.Add(tabInventory);

            // Scan installed apps lazily the first time the Inventory tab is shown
            // (winget list takes a few seconds, so don't pay it on every launch).
            tabs.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (!ReferenceEquals(e.OriginalSource, tabs)) return;   // ignore inner selectors
                if (tabs.SelectedItem == tabInventory && !appScanned) { appScanned = true; ScanApps(); }
            };

            // Top bar with Help and About, kept visible above both tabs.
            var btnHelp = MakeButton("_Help", "Open the GUARD help and readme", delegate { OpenHelp(); });
            var btnAbout = MakeButton("_About", "About GUARD", delegate { ShowAbout(); });
            btnHelp.Margin = new Thickness(0, 0, 6, 0);
            var topBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 6, 0) };
            topBar.Children.Add(btnHelp);
            topBar.Children.Add(btnAbout);
            var dock = new DockPanel();
            DockPanel.SetDock(topBar, Dock.Top);
            dock.Children.Add(topBar);
            dock.Children.Add(tabs);
            Content = dock;

            RefreshNextRun();

            // Initial UI population fired TextChanged/Checked handlers; clear the
            // resulting "dirty" flag so the status reflects the on-disk script.
            dirty = false;
            RefreshScriptStatus();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyTitleBar(this);
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (src != null) src.AddHook(WndHook);
        }

        IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_SETTINGCHANGE (0x001A): fired when the user flips dark/light mode.
            if (msg == 0x001A)
            {
                Theme.Reapply(Application.Current);
                Theme.ApplyTitleBar(this);
            }
            return IntPtr.Zero;
        }

        // =====================================================================
        //  TAB 1 - FILE BACKUP
        // =====================================================================
        UIElement BuildFileTab()
        {
            var panel = new StackPanel { Width = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14) };

            // --- Backup destination (Browse + Test sit on the same row) ---
            // Any folder is valid: a local drive, an external disk, or a network
            // share. Browse opens a folder picker; the box also accepts a typed
            // or pasted path (including a UNC path like \\server\share).
            txtDest = new TextBox { Text = cfg.Dest };
            AutomationProperties.SetName(txtDest, "Backup destination path");
            var btnBrowseDest = MakeButton("_Browse...", "Browse for the backup destination folder",
                delegate { BrowseInto(txtDest); });
            var btnTest = MakeButton("_Test", "Test the backup destination",
                delegate { TestConnection(txtDest.Text); });
            panel.Children.Add(PathRow("Backup _destination:", txtDest, btnBrowseDest, btnTest));

            // --- Folders list ---
            // A list of real CheckBoxes (not a DataGrid). The element you arrow
            // onto IS the checkbox, so NVDA announces its checked/unchecked state
            // natively and Space toggles it. (A DataGrid row only exposes its
            // selection state - "selected" - which is unrelated to the checkbox,
            // exactly the confusion the user reported.)
            var lblFolders = Caption("F_olders to back up (tick to include):", null);
            panel.Children.Add(lblFolders);
            panel.Children.Add(ColumnHeader(new[] { "Source folder", "Subfolder" }, new[] { 360.0, 200.0 }));
            string folderRowXaml =
                "<CheckBox Margin='3,2' HorizontalAlignment='Stretch'" +
                " IsChecked='{Binding Include, Mode=TwoWay}' a:AutomationProperties.Name='{Binding Caption}'>" +
                ColumnGrid(new[] { "Source", "SubFolder" }, new[] { 360.0, 200.0 }) +
                "</CheckBox>";
            var folderBorder = MakeCheckList(cfg.Folders, "Folders to back up", 170, folderRowXaml, out folderList);
            lblFolders.Target = folderList;
            // Remember which folder's checkbox last held focus so Remove knows the target.
            folderList.AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(
                delegate(object s, KeyboardFocusChangedEventArgs e)
                {
                    var fe = e.NewFocus as FrameworkElement;
                    if (fe != null && fe.DataContext is FolderPair) currentFolder = (FolderPair)fe.DataContext;
                }));
            panel.Children.Add(folderBorder);

            var btnAddF = MakeButton("A_dd Folder...", "Add a folder to back up", delegate { AddFolder(); });
            var btnRemF = MakeButton("_Remove Folder", "Remove the selected folder", delegate { RemoveFolder(); });
            panel.Children.Add(Row(btnAddF, btnRemF));

            // --- Mode ---
            var grpMode = new GroupBox { Header = "Mode" };
            AutomationProperties.SetName(grpMode, "Backup mode");
            var modePanel = new StackPanel();
            rbAdditive = new RadioButton { Content = "Add_itive  (copy new and changed files; nothing at the destination is ever deleted)", IsChecked = cfg.Mode != "Mirror" };
            AutomationProperties.SetName(rbAdditive, "Additive mode. Copies new and changed files. Nothing at the destination is ever deleted.");
            rbMirror = new RadioButton { Content = "_Mirror  (make the destination match the source exactly; files deleted from the source are also deleted at the destination)", IsChecked = cfg.Mode == "Mirror" };
            AutomationProperties.SetName(rbMirror, "Mirror mode. Makes the destination match the source exactly. Files deleted from the source are also deleted at the destination.");
            modePanel.Children.Add(rbAdditive);
            modePanel.Children.Add(rbMirror);
            grpMode.Content = modePanel;
            panel.Children.Add(grpMode);

            // --- Excludes (side by side) ---
            var exGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            exGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            exGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            exGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            txtExDirs = new TextBox
            {
                Text = cfg.ExcludeDirs, Height = 64, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap
            };
            AutomationProperties.SetName(txtExDirs, "Exclude folder names, one per line");
            txtExFiles = new TextBox
            {
                Text = cfg.ExcludeFiles, Height = 64, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap
            };
            AutomationProperties.SetName(txtExFiles, "Exclude file names, one per line");

            var lblExDirs = new Label { Content = "Exclude folder _names (one per line):", Target = txtExDirs };
            var lblExFiles = new Label { Content = "Exclude file na_mes (one per line):", Target = txtExFiles };
            Grid.SetRow(lblExDirs, 0); Grid.SetColumn(lblExDirs, 0);
            Grid.SetRow(lblExFiles, 0); Grid.SetColumn(lblExFiles, 2);
            Grid.SetRow(txtExDirs, 1); Grid.SetColumn(txtExDirs, 0);
            Grid.SetRow(txtExFiles, 1); Grid.SetColumn(txtExFiles, 2);
            exGrid.Children.Add(lblExDirs); exGrid.Children.Add(lblExFiles);
            exGrid.Children.Add(txtExDirs); exGrid.Children.Add(txtExFiles);
            panel.Children.Add(exGrid);

            // --- Schedule ---
            var grpSched = new GroupBox { Header = "Schedule" };
            AutomationProperties.SetName(grpSched, "Backup schedule");
            var schedPanel = new StackPanel();
            var schedRow = new StackPanel { Orientation = Orientation.Horizontal };
            chkSchedule = new CheckBox { Content = "Run daily at:", IsChecked = cfg.ScheduleEnabled, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(chkSchedule, "Run daily");
            txtTime = new TextBox { Text = cfg.ScheduleTime, Width = 60, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(txtTime, "Daily run time, 24-hour HH colon mm");
            var btnMakeTask = MakeButton("Create / _Update Task", "Create or update the daily scheduled task",
                delegate { if (SaveAll()) UpdateFileTask(); });
            btnMakeTask.Margin = new Thickness(12, 0, 6, 0);
            var btnRemTask = MakeButton("Remove _Task", "Remove the daily scheduled task",
                delegate { RemoveTask(FileTaskName, RefreshNextRun); });
            schedRow.Children.Add(chkSchedule);
            schedRow.Children.Add(txtTime);
            schedRow.Children.Add(btnMakeTask);
            schedRow.Children.Add(btnRemTask);
            lblNextRun = new Label { Content = "Next run: (unknown)", Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(lblNextRun, "Next scheduled run");
            schedPanel.Children.Add(schedRow);
            schedPanel.Children.Add(lblNextRun);
            grpSched.Content = schedPanel;
            panel.Children.Add(grpSched);

            // --- Action buttons (Save Settings first; it writes the script the others use) ---
            var btnSave = MakeButton("_Save Settings", "Save settings and regenerate the backup script and scheduled task",
                delegate { if (SaveAll()) MessageBox.Show("Settings saved. The backup script and scheduled task have been updated.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information); });
            var btnRun = MakeButton("_Run Now", "Run the backup now", delegate { RunScript(ScriptPath, "", txtOutput, fileProgress, fileProgressLabel); });
            var btnPrev = MakeButton("_Preview", "Preview the backup without making changes", delegate { RunScript(ScriptPath, "test", txtOutput, fileProgress, fileProgressLabel); });
            var btnLog = MakeButton("Open _Last Log", "Open the last backup log", delegate { OpenPath(LogPath); });
            var btnDest = MakeButton("Open _Destination", "Open the backup destination folder", delegate { OpenPath(txtDest.Text); });
            panel.Children.Add(Row(btnSave, btnRun, btnPrev, btnLog, btnDest));

            // --- Settings status line: coloured dot + normal-contrast text ---
            scriptDot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            scriptStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            scriptStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            // Live region: NVDA announces the new wording whenever the status
            // changes (for example right after a save), without the user having
            // to navigate to it. Status lines are not normally in the Tab order.
            AutomationProperties.SetLiveSetting(scriptStatusText, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(scriptStatusText, "Settings status");
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            statusRow.Children.Add(scriptDot);
            statusRow.Children.Add(scriptStatusText);
            AutomationProperties.SetName(statusRow, "Settings status");
            panel.Children.Add(statusRow);

            // --- Progress (per folder) + output pane ---
            fileProgressLabel = new Label { Content = "", Margin = new Thickness(0, 8, 0, 2) };
            AutomationProperties.SetName(fileProgressLabel, "Backup progress");
            panel.Children.Add(fileProgressLabel);
            fileProgress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 0, 0, 2) };
            AutomationProperties.SetName(fileProgress, "Backup progress bar");
            panel.Children.Add(fileProgress);

            panel.Children.Add(Caption("Out_put:", null));
            txtOutput = MakeOutputBox(150);
            AutomationProperties.SetName(txtOutput, "Backup output");
            panel.Children.Add(txtOutput);

            // Track unsaved edits so the status line can prompt a regenerate.
            txtDest.TextChanged += MarkDirty;
            txtExDirs.TextChanged += MarkDirty;
            txtExFiles.TextChanged += MarkDirty;
            txtTime.TextChanged += MarkDirty;
            rbMirror.Checked += MarkDirtyR; rbMirror.Unchecked += MarkDirtyR;
            rbAdditive.Checked += MarkDirtyR; rbAdditive.Unchecked += MarkDirtyR;
            chkSchedule.Checked += MarkDirtyR; chkSchedule.Unchecked += MarkDirtyR;
            WireFolderDirty();

            return WrapScroll(panel);
        }

        void AddFolder()
        {
            var dlg = new FolderWindow(null) { Owner = this };
            if (dlg.ShowDialog() == true)
                cfg.Folders.Add(new FolderPair(true, dlg.SourcePath, dlg.SubFolder));
        }

        void RemoveFolder()
        {
            // currentFolder is updated whenever a folder checkbox receives focus.
            var f = currentFolder;
            if (f == null)
            {
                MessageBox.Show("Tab into the folder list and arrow to the folder you want to remove, then press Remove Folder.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Remove this folder from the backup?\r\n\r\n" + f.Source, "GUARD",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                cfg.Folders.Remove(f);
                currentFolder = null;
            }
        }

        // ---- unsaved-changes tracking + script-status indicator -------------
        void MarkDirty(object s, TextChangedEventArgs e) { dirty = true; RefreshScriptStatus(); }
        void MarkDirtyR(object s, RoutedEventArgs e) { dirty = true; RefreshScriptStatus(); }

        // Folder add/remove and each row's Include toggle all count as changes.
        void WireFolderDirty()
        {
            cfg.Folders.CollectionChanged += delegate(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.NewItems != null)
                    foreach (FolderPair f in e.NewItems) f.PropertyChanged += FolderItemChanged;
                dirty = true; RefreshScriptStatus();
            };
            foreach (var f in cfg.Folders) f.PropertyChanged += FolderItemChanged;
        }
        void FolderItemChanged(object s, PropertyChangedEventArgs e) { dirty = true; RefreshScriptStatus(); }

        void RefreshScriptStatus()
        {
            if (scriptDot == null || scriptStatusText == null) return;
            Color green = Color.FromRgb(0x3F, 0xB9, 0x50);
            Color amber = Color.FromRgb(0xD2, 0x99, 0x22);
            if (!File.Exists(ScriptPath))
            {
                scriptDot.Fill = new SolidColorBrush(amber);
                scriptStatusText.Text = "No settings saved yet. Click Save Settings before running a backup.";
            }
            else if (dirty)
            {
                scriptDot.Fill = new SolidColorBrush(amber);
                scriptStatusText.Text = "You have unsaved changes. Click Save Settings to apply them.";
            }
            else
            {
                scriptDot.Fill = new SolidColorBrush(green);
                scriptStatusText.Text = "Settings saved. Last updated " +
                    File.GetLastWriteTime(ScriptPath).ToString("yyyy-MM-dd HH:mm") + ".";
            }
            AnnounceStatus(scriptStatusText);
        }

        // Nudge UI Automation to re-read a live region so NVDA speaks the new
        // text the moment it changes. Safe to call before the window is shown.
        static void AnnounceStatus(UIElement el)
        {
            try
            {
                var peer = UIElementAutomationPeer.FromElement(el);
                if (peer == null) peer = UIElementAutomationPeer.CreatePeerForElement(el);
                if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch { }
        }

        // =====================================================================
        //  TAB 2 - APP INVENTORY
        // =====================================================================
        UIElement BuildInventoryTab()
        {
            var panel = new StackPanel { Width = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14) };

            panel.Children.Add(new TextBlock
            {
                Text = "Installed apps are read from the Windows uninstall registry, and cross-checked " +
                    "against winget when it is available. Apps winget can reinstall are marked \"Winget\"; " +
                    "the rest are marked \"Manual\". Tick the apps you want to keep, then Export the list to " +
                    "your chosen destination. After an OS reinstall, Import the list and Reinstall Selected " +
                    "to put the Winget ones back automatically. The exported file is plain JSON, so you can " +
                    "also open it to read your app list by hand.",
                Margin = new Thickness(0, 0, 0, 8)
            });

            // --- Export destination (Browse + Test sit on the same row) ---
            txtAppDest = new TextBox { Text = cfg.AppListDest };
            AutomationProperties.SetName(txtAppDest, "App list destination path");
            var btnBrowseApp = MakeButton("_Browse...", "Browse for the app list destination folder",
                delegate { BrowseInto(txtAppDest); });
            var btnTest = MakeButton("_Test", "Test the app list destination",
                delegate { TestConnection(txtAppDest.Text); });
            panel.Children.Add(PathRow("List de_stination:", txtAppDest, btnBrowseApp, btnTest));

            // --- Toolbar: refresh, select all/none, filter ---
            btnAppRefresh = MakeButton("_Refresh List", "Rescan installed apps", delegate { appScanned = true; ScanApps(); });
            btnAppAll = MakeButton("Select _All", "Tick all visible apps", delegate { AppSelect(true); });
            btnAppNone = MakeButton("Select _None", "Untick all visible apps", delegate { AppSelect(false); });
            txtAppFilter = new TextBox { Width = 180, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(txtAppFilter, "Filter apps by name");
            txtAppFilter.TextChanged += delegate
            {
                appFilter = txtAppFilter.Text.Trim();
                if (appView != null) appView.Refresh();
            };
            var lblFilter = new Label { Content = "_Filter:", Target = txtAppFilter, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var c in new UIElement[] { btnAppRefresh, btnAppAll, btnAppNone, lblFilter, txtAppFilter })
            {
                if (c is FrameworkElement && !(c is Label)) ((FrameworkElement)c).Margin = new Thickness(0, 0, 6, 0);
                toolRow.Children.Add(c);
            }
            panel.Children.Add(toolRow);

            // --- App list (checkbox rows; same NVDA-friendly pattern as folders) ---
            panel.Children.Add(ColumnHeader(new[] { "Application", "Version", "Source" }, new[] { 300.0, 110.0, 130.0 }));
            string appRowXaml =
                "<CheckBox Margin='3,2' HorizontalAlignment='Stretch'" +
                " IsChecked='{Binding Include, Mode=TwoWay}' a:AutomationProperties.Name='{Binding Caption}'>" +
                ColumnGrid(new[] { "Name", "Version", "SourceLabel" }, new[] { 300.0, 110.0, 130.0 }) +
                "</CheckBox>";
            var appBorder = MakeCheckList(appRows, "Installed applications", 240, appRowXaml, out appList);
            appView = CollectionViewSource.GetDefaultView(appRows);
            appView.Filter = AppFilterPredicate;
            panel.Children.Add(appBorder);

            // --- Status line ---
            appStatus = new Label { Content = "Open this tab to scan installed apps.", Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(appStatus, "Inventory status");
            panel.Children.Add(appStatus);

            // --- Action buttons ---
            btnAppExport = MakeButton("_Export List", "Export the ticked apps to the destination", delegate { ExportApps(); });
            btnAppImport = MakeButton("_Import List", "Import an app list from a file", delegate { ImportApps(); });
            btnAppReinstall = MakeButton("Reinstall _Selected", "Reinstall the ticked Winget apps", delegate { ReinstallSelected(); });
            var btnAppOpen = MakeButton("_Open Folder", "Open the app list destination folder", delegate { OpenPath(txtAppDest.Text); });
            panel.Children.Add(Row(btnAppExport, btnAppImport, btnAppReinstall, btnAppOpen));

            panel.Children.Add(DimNote("Note: Reinstalling may require Administrator rights. If installs fail with an access error, " +
                "restart GUARD as Administrator. Portable apps (run from a folder, no installer) are not auto-detected."));

            // --- Reinstall progress + output ---
            appProgressLabel = new Label { Content = "", Margin = new Thickness(0, 8, 0, 2) };
            AutomationProperties.SetName(appProgressLabel, "Reinstall progress");
            panel.Children.Add(appProgressLabel);
            appProgress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 0, 0, 2) };
            AutomationProperties.SetName(appProgress, "Reinstall progress bar");
            panel.Children.Add(appProgress);

            panel.Children.Add(Caption("Outp_ut:", null));
            txtAppOutput = MakeOutputBox(120);
            AutomationProperties.SetName(txtAppOutput, "Inventory output");
            panel.Children.Add(txtAppOutput);

            return WrapScroll(panel);
        }

        bool AppFilterPredicate(object o)
        {
            var a = o as AppEntry;
            if (a == null) return false;
            if (appFilter.Length == 0) return true;
            return a.Name != null &&
                a.Name.ToLowerInvariant().IndexOf(appFilter.ToLowerInvariant(), StringComparison.Ordinal) >= 0;
        }

        void AppSelect(bool value)
        {
            if (appView == null) return;
            foreach (AppEntry a in appView) a.Include = value;
        }

        void SetAppBusy(bool busy)
        {
            bool e = !busy;
            if (btnAppRefresh != null) btnAppRefresh.IsEnabled = e;
            if (btnAppExport != null) btnAppExport.IsEnabled = e;
            if (btnAppImport != null) btnAppImport.IsEnabled = e;
            if (btnAppReinstall != null) btnAppReinstall.IsEnabled = e;
            if (btnAppAll != null) btnAppAll.IsEnabled = e;
            if (btnAppNone != null) btnAppNone.IsEnabled = e;
        }

        // ---- background scan of installed apps ------------------------------
        void ScanApps()
        {
            if (scanning) return;
            scanning = true;
            SetAppBusy(true);
            appStatus.Content = "Scanning installed apps (this can take a few seconds)...";
            var th = new Thread(delegate()
            {
                List<AppEntry> found = null; string err = null;
                try { found = DetectApps(); }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (err != null) { appStatus.Content = "Scan failed: " + err; }
                    else
                    {
                        appRows.Clear();
                        int auto = 0, man = 0;
                        foreach (var a in found) { appRows.Add(a); if (a.CanAuto) auto++; else man++; }
                        if (wingetAvailable)
                            appStatus.Content = found.Count + " apps found. " + auto + " reinstallable via winget, " + man + " manual.";
                        else
                            appStatus.Content = found.Count + " apps found. winget is not installed, so apps cannot be reinstalled automatically. You can still export the list for reference.";
                        if (appView != null) appView.Refresh();
                    }
                    scanning = false; SetAppBusy(false);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // Detect installed apps. The uninstall registry is the primary source
        // (always present, with accurate metadata), so every app shows up there.
        // winget is an optional enrichment layer: when present, its package ids
        // tell us which apps can be reinstalled automatically, and it can add a
        // few Store/MSIX apps that are not in the registry. When winget is
        // missing the registry list still stands on its own (all "manual").
        List<AppEntry> DetectApps()
        {
            var ordered = new List<AppEntry>();
            var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

            // 1. Registry: the always-present base list.
            foreach (var kv in ReadRegistryApps())
            {
                var ri = kv.Value;
                var e = new AppEntry();
                e.Name = ri.DisplayName;
                e.Version = ri.Version;
                e.Publisher = ri.Publisher;
                e.InstallLocation = ri.InstallLocation;
                e.PublisherUrl = ri.Url;
                e.Source = "manual";
                e.Id = "";
                e.Include = true;
                if (!byName.ContainsKey(e.Name)) { byName[e.Name] = e; ordered.Add(e); }
            }

            // 2. winget enrichment. RunCapture throws when winget is not on PATH,
            //    so a missing winget simply leaves the registry list as-is.
            wingetAvailable = false;
            string raw = null;
            try { raw = RunCapture("winget", "list --accept-source-agreements"); wingetAvailable = true; }
            catch { wingetAvailable = false; }

            if (wingetAvailable && raw != null)
            {
                // winget prints a spinner preamble that is carriage-return-only (no
                // line feed). Stripping CR and splitting on LF alone would merge that
                // whole preamble into the header line and push the Id column offset
                // hundreds of chars to the right. Split on both CR and LF (dropping
                // empties) so the header stands alone.
                string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                int hi = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (l.IndexOf("Name", StringComparison.Ordinal) >= 0 &&
                        l.IndexOf("Id", StringComparison.Ordinal) >= 0 &&
                        l.IndexOf("Version", StringComparison.Ordinal) >= 0)
                    { hi = i; break; }
                }
                if (hi >= 0)
                {
                    int idStart = lines[hi].IndexOf("Id", StringComparison.Ordinal);
                    int start = hi + 1;
                    if (start < lines.Length && lines[start].TrimStart().StartsWith("---")) start++;

                    for (int i = start; i < lines.Length; i++)
                    {
                        string row = lines[i];
                        if (row.Trim().Length == 0) continue;

                        // Name occupies a fixed left column; winget truncates it to fit,
                        // so it never bleeds past idStart. The tail (id, version, ...) has
                        // no spaces inside each token, so tokenise it rather than slice;
                        // that stays correct even when wide glyphs shift the columns.
                        string name = (idStart <= row.Length ? row.Substring(0, idStart) : row).Trim();
                        name = name.TrimEnd('…').Trim();   // strip trailing ellipsis
                        if (name.Length == 0) continue;

                        string tail = row.Length > idStart ? row.Substring(idStart) : "";
                        var toks = Regex.Matches(tail, "\\S+");
                        string id = toks.Count > 0 ? toks[0].Value : "";
                        string ver = toks.Count > 1 ? toks[1].Value : "";
                        if (ver == "…") ver = "";
                        bool auto = id.Length > 0 && !id.StartsWith("ARP\\") && id.IndexOf('\\') < 0 && !id.EndsWith("…");

                        AppEntry existing;
                        if (byName.TryGetValue(name, out existing))
                        {
                            // Registry already lists this app; let a real winget id
                            // upgrade it from manual to reinstallable.
                            if (auto && !existing.CanAuto) { existing.Source = "winget"; existing.Id = id; }
                            if (existing.Version.Length == 0 && ver.Length > 0) existing.Version = ver;
                        }
                        else if (auto)
                        {
                            // A winget/Store app with no uninstall-registry entry (e.g. MSIX).
                            var e = new AppEntry();
                            e.Name = name;
                            e.Version = ver;
                            e.Source = "winget";
                            e.Id = id;
                            e.Include = true;
                            byName[name] = e;
                            ordered.Add(e);
                        }
                    }
                }
            }

            ordered.Sort(delegate(AppEntry a, AppEntry b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return ordered;
        }

        class RegInfo { public string DisplayName = ""; public string Version = ""; public string Publisher = ""; public string InstallLocation = ""; public string Url = ""; }

        Dictionary<string, RegInfo> ReadRegistryApps()
        {
            var d = new Dictionary<string, RegInfo>();
            AddRegRoot(d, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            AddRegRoot(d, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            AddRegRoot(d, Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            return d;
        }

        static void AddRegRoot(Dictionary<string, RegInfo> d, RegistryKey root, string path)
        {
            try
            {
                using (var k = root.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                var name = s.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(name)) continue;
                                object sysc = s.GetValue("SystemComponent");
                                if (sysc is int && (int)sysc == 1) continue;        // hidden system entry
                                if (s.GetValue("ParentKeyName") != null) continue;  // update/child entry
                                var key = name.ToLowerInvariant();
                                if (d.ContainsKey(key)) continue;
                                var ri = new RegInfo();
                                ri.DisplayName = name;
                                ri.Version = (s.GetValue("DisplayVersion") as string) ?? "";
                                ri.Publisher = (s.GetValue("Publisher") as string) ?? "";
                                ri.InstallLocation = (s.GetValue("InstallLocation") as string) ?? "";
                                ri.Url = (s.GetValue("URLInfoAbout") as string) ?? "";
                                d[key] = ri;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // ---- export / import the app list -----------------------------------
        void ExportApps()
        {
            HarvestUi();
            if (string.IsNullOrEmpty(cfg.AppListDest))
            {
                MessageBox.Show("Enter an app list destination first.\r\n\r\nType a folder path next to \"List destination\", or use the Browse button to pick one.",
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var picked = new List<AppEntry>();
            foreach (var a in appRows) if (a.Include) picked.Add(a);
            if (picked.Count == 0)
            {
                MessageBox.Show("Tick at least one app to export.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                if (!Directory.Exists(cfg.AppListDest)) Directory.CreateDirectory(cfg.AppListDest);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Destination is not reachable:\r\n" + cfg.AppListDest + "\r\n\r\n" + ex.Message,
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var file = new AppListFile();
            file.Exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            file.Machine = Environment.MachineName;
            var items = new List<AppListItem>();
            foreach (var a in picked)
            {
                var it = new AppListItem();
                it.Name = a.Name; it.Id = a.Id; it.Source = a.Source; it.Version = a.Version;
                it.Publisher = a.Publisher; it.InstallLocation = a.InstallLocation; it.PublisherUrl = a.PublisherUrl;
                items.Add(it);
            }
            file.Apps = items.ToArray();

            string path = Path.Combine(cfg.AppListDest, AppListFileName);
            try
            {
                WriteJson(path, file);
                WriteIni();   // remember the destination so it survives a restart
                MessageBox.Show("Exported " + picked.Count + " apps to:\r\n" + path, "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not write the app list:\r\n" + path + "\r\n\r\n" + ex.Message,
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void ImportApps()
        {
            HarvestUi();
            var dlg = new OpenFileDialog();
            dlg.Title = "Import app list";
            dlg.Filter = "App list (*.json)|*.json|All files (*.*)|*.*";
            try
            {
                if (Directory.Exists(cfg.AppListDest))
                {
                    dlg.InitialDirectory = cfg.AppListDest;
                    if (File.Exists(Path.Combine(cfg.AppListDest, AppListFileName))) dlg.FileName = AppListFileName;
                }
            }
            catch { }
            if (dlg.ShowDialog() != true) return;

            AppListFile f;
            try { f = ReadJson(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show("That file could not be read as an app list:\r\n\r\n" + ex.Message,
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (f == null || f.Apps == null || f.Apps.Length == 0)
            {
                MessageBox.Show("The app list is empty.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            appRows.Clear();
            int auto = 0, man = 0;
            foreach (var it in f.Apps)
            {
                var a = new AppEntry();
                a.Name = it.Name ?? "";
                a.Id = it.Id ?? "";
                a.Source = string.IsNullOrEmpty(it.Source) ? (string.IsNullOrEmpty(it.Id) ? "manual" : "winget") : it.Source;
                a.Version = it.Version ?? "";
                a.Publisher = it.Publisher ?? "";
                a.InstallLocation = it.InstallLocation ?? "";
                a.PublisherUrl = it.PublisherUrl ?? "";
                a.Include = true;
                if (a.CanAuto) auto++; else man++;
                appRows.Add(a);
            }
            appScanned = true;
            string mac = f.Machine ?? "?";
            string exp = f.Exported ?? "";
            appStatus.Content = "Imported " + f.Apps.Length + " apps from " + mac +
                (exp.Length > 0 ? " (" + exp + ")" : "") + ". " + auto + " reinstallable, " + man + " manual.";
            if (appView != null) appView.Refresh();
        }

        static void WriteJson(string path, AppListFile f)
        {
            var ser = new DataContractJsonSerializer(typeof(AppListFile));
            using (var fs = File.Create(path))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
                ser.WriteObject(w, f);
        }

        static AppListFile ReadJson(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(AppListFile));
            using (var fs = File.OpenRead(path))
                return (AppListFile)ser.ReadObject(fs);
        }

        // ---- reinstall the ticked winget apps (sequentially) ----------------
        void ReinstallSelected()
        {
            if (reinstalling)
            {
                MessageBox.Show("A reinstall is already running. Wait for it to finish.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var targets = new List<AppEntry>();
            int manual = 0;
            foreach (var a in appRows)
            {
                if (!a.Include) continue;
                if (a.CanAuto && !string.IsNullOrEmpty(a.Id)) targets.Add(a);
                else manual++;
            }
            if (targets.Count == 0)
            {
                MessageBox.Show("None of the ticked apps can be reinstalled automatically.\r\n\r\n" +
                    "Only \"Winget\" apps reinstall automatically; \"Manual\" apps must be reinstalled by hand.",
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string msg = "Reinstall " + targets.Count + " app(s) via winget, one at a time?";
            if (manual > 0) msg += "\r\n\r\n" + manual + " ticked \"Manual\" app(s) will be skipped (install those by hand).";
            msg += "\r\n\r\nThis may require Administrator rights.";
            if (MessageBox.Show(msg, "GUARD", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            reinstalling = true;
            SetAppBusy(true);
            txtAppOutput.Clear();
            SetProgress(appProgress, appProgressLabel, targets.Count, 0, "Starting...");

            var th = new Thread(delegate()
            {
                int ok = 0, fail = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    var app = targets[i];
                    int idx = i;
                    SetProgress(appProgress, appProgressLabel, targets.Count, idx,
                        "Installing: " + app.Name + " (" + (idx + 1) + " of " + targets.Count + ")");
                    AppendOut(txtAppOutput, "\r\n=== Installing " + app.Name + "  [" + app.Id + "] ===\r\n");
                    int code;
                    try { code = RunWingetInstall(app.Id, txtAppOutput); }
                    catch (Exception ex) { AppendOut(txtAppOutput, "ERROR: " + ex.Message + "\r\n"); code = -1; }
                    if (code == 0) ok++; else fail++;
                    SetProgress(appProgress, appProgressLabel, targets.Count, idx + 1, "");
                }
                int okF = ok, failF = fail;
                Dispatcher.BeginInvoke((Action)delegate
                {
                    appProgressLabel.Content = "Done. " + okF + " installed, " + failF + " failed.";
                    AppendOut(txtAppOutput, "\r\n--- Reinstall complete: " + okF + " installed, " + failF + " failed ---\r\n");
                    reinstalling = false;
                    reinstallProc = null;
                    SetAppBusy(false);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        int RunWingetInstall(string id, TextBox outBox)
        {
            var psi = new ProcessStartInfo("winget",
                "install --id \"" + id + "\" -e --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendOut(outBox, e.Data + "\r\n"); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendOut(outBox, e.Data + "\r\n"); };
                p.Start();
                reinstallProc = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        // Capture stdout of a console tool (used for `winget list`).
        static string RunCapture(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return o;
            }
        }

        // =====================================================================
        //  UI HELPERS
        // =====================================================================
        static ScrollViewer WrapScroll(UIElement content)
        {
            return new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0)
            };
        }

        static Grid PathRow(string labelText, TextBox box, params Button[] trailing)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new Label { Content = labelText, Target = box, VerticalAlignment = VerticalAlignment.Center };
            box.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lbl, 0); Grid.SetColumn(box, 1);
            g.Children.Add(lbl); g.Children.Add(box);
            if (trailing != null && trailing.Length > 0)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                for (int i = 0; i < trailing.Length; i++)
                {
                    if (i > 0) trailing[i].Margin = new Thickness(6, 0, 0, 0);
                    sp.Children.Add(trailing[i]);
                }
                Grid.SetColumn(sp, 2);
                g.Children.Add(sp);
            }
            return g;
        }

        static Label Caption(string text, UIElement target)
        {
            var lbl = new Label { Content = text, Margin = new Thickness(0, 8, 0, 2) };
            if (target is Control) lbl.Target = (Control)target;
            return lbl;
        }

        TextBlock DimNote(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            AutomationProperties.SetName(tb, text);
            return tb;
        }

        static StackPanel Row(params UIElement[] items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var it in items)
            {
                if (it is FrameworkElement) ((FrameworkElement)it).Margin = new Thickness(0, 0, 6, 0);
                sp.Children.Add(it);
            }
            return sp;
        }

        static Button MakeButton(string content, string autoName, RoutedEventHandler onClick)
        {
            var b = new Button { Content = content, MinWidth = 80 };
            AutomationProperties.SetName(b, autoName);
            b.Click += onClick;
            return b;
        }

        TextBox MakeOutputBox(double height)
        {
            return new TextBox
            {
                Height = height, IsReadOnly = true, AcceptsReturn = true,
                // A read-only TextBox normally has no system caret, so NVDA cannot
                // follow Up/Down arrows and just re-reads the first line. Showing
                // the read-only caret gives NVDA a moving caret to track, so the
                // output can be reviewed line by line.
                IsReadOnlyCaretVisible = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap
            };
        }

        // Creates a scrollable bordered list of CheckBoxes from an observable
        // collection. Each item IS a CheckBox, so NVDA announces:
        //   "[Caption], check box, checked / unchecked"
        // as you arrow through; the checked/unchecked state is part of the
        // CheckBox role itself, not a separate selection state.
        //
        // DirectionalNavigation="Contained" keeps Up/Down arrows inside the
        // list. TabNavigation="Once" means Tab enters the list (landing on the
        // first checkbox), then a second Tab exits it, same as a normal listbox.
        static Border MakeCheckList(System.Collections.IEnumerable source,
                                    string name, double height,
                                    string itemXaml, out ItemsControl list)
        {
            const string Ns =
                "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
                " xmlns:a='clr-namespace:System.Windows.Automation;assembly=PresentationCore'";
            var dt = (DataTemplate)XamlReader.Parse("<DataTemplate " + Ns + ">" + itemXaml + "</DataTemplate>");

            list = new ItemsControl { ItemsSource = source, ItemTemplate = dt, Focusable = false };
            KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Once);
            AutomationProperties.SetName(list, name);

            var sv = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            var border = new Border { Child = sv, BorderThickness = new Thickness(1), Height = height };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            return border;
        }

        // Visual column-header row above a CheckList. leftPad offsets the
        // headers to align with the text content that starts after the checkbox
        // indicator (~16px box + ~6px gap + 3px item margin = 25px, use 26).
        static FrameworkElement ColumnHeader(string[] headers, double[] widths)
        {
            const double LeftPad = 26;
            var g = new Grid { Margin = new Thickness(LeftPad, 4, 0, 1) };
            for (int i = 0; i < widths.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
            for (int i = 0; i < headers.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = headers[i],
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                Grid.SetColumn(tb, i);
                g.Children.Add(tb);
            }
            return g;
        }

        // Generates an inline XAML fragment: a Grid whose columns match the
        // supplied widths, each populated by a TextBlock bound to the matching
        // property name. Embedded inside the DataTemplate passed to MakeCheckList.
        static string ColumnGrid(string[] bindings, double[] widths)
        {
            var sb = new StringBuilder();
            sb.Append("<Grid><Grid.ColumnDefinitions>");
            for (int i = 0; i < widths.Length; i++)
                sb.Append("<ColumnDefinition Width='" + widths[i].ToString(CultureInfo.InvariantCulture) + "'/>");
            sb.Append("</Grid.ColumnDefinitions>");
            for (int i = 0; i < bindings.Length; i++)
                sb.Append("<TextBlock Grid.Column='" + i + "' Text='{Binding " + bindings[i] + "}' Margin='0,0,8,0' VerticalAlignment='Center'/>");
            sb.Append("</Grid>");
            return sb.ToString();
        }

        // =====================================================================
        //  SETTINGS LOAD / SAVE
        // =====================================================================
        void LoadSettings()
        {
            cfg = new Settings();
            cfg.Folders = Settings.DefaultFolders();
            if (!File.Exists(IniPath)) return;

            var section = "";
            var folders = new ObservableCollection<FolderPair>();
            bool sawFolders = false;
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1);

                if (section == "Folders")
                {
                    sawFolders = true;
                    var parts = val.Split('|');
                    if (parts.Length >= 3)
                        folders.Add(new FolderPair(parts[0] == "1", parts[1], parts[2]));
                    continue;
                }

                switch (section + "." + key)
                {
                    case "General.Dest": cfg.Dest = val; break;
                    case "General.Mode": cfg.Mode = val; break;
                    case "General.ExcludeDirs": cfg.ExcludeDirs = Unescape(val); break;
                    case "General.ExcludeFiles": cfg.ExcludeFiles = Unescape(val); break;
                    case "Schedule.Enabled": cfg.ScheduleEnabled = val == "1"; break;
                    case "Schedule.Time": cfg.ScheduleTime = val; break;
                    case "AppList.Dest": cfg.AppListDest = val; break;
                }
            }
            if (sawFolders) cfg.Folders = folders;
        }

        // Pull current UI state into cfg, then write ini + script + task.
        // Returns false (after showing a message) when a required value is
        // missing, so Save / Run / Preview can stop before doing anything.
        bool SaveAll()
        {
            HarvestUi();
            if (string.IsNullOrEmpty(cfg.Dest))
            {
                MessageBox.Show("Enter a backup destination first.\r\n\r\nType a folder path next to \"Backup destination\", or use the Browse button to pick one.",
                    "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            WriteIni();
            WriteScript();
            // The daily file task needs no elevation, so keep it in sync on every Save.
            if (cfg.ScheduleEnabled) UpdateFileTask(); else RemoveTask(FileTaskName, RefreshNextRun, true);
            // The on-disk script now matches the settings.
            dirty = false;
            RefreshScriptStatus();
            return true;
        }

        void HarvestUi()
        {
            cfg.Dest = txtDest.Text.Trim();
            cfg.Mode = rbMirror.IsChecked == true ? "Mirror" : "Additive";
            cfg.ExcludeDirs = txtExDirs.Text;
            cfg.ExcludeFiles = txtExFiles.Text;
            cfg.ScheduleEnabled = chkSchedule.IsChecked == true;
            cfg.ScheduleTime = NormalizeTime(txtTime.Text, cfg.ScheduleTime);
            // Folder include flags are already current via two-way binding.

            if (txtAppDest != null) cfg.AppListDest = txtAppDest.Text.Trim();
        }

        void WriteIni()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; GUARD settings - generated file. Edit via GUARD.exe.");
            sb.AppendLine("[General]");
            sb.AppendLine("Dest=" + cfg.Dest);
            sb.AppendLine("Mode=" + cfg.Mode);
            sb.AppendLine("ExcludeDirs=" + Escape(cfg.ExcludeDirs));
            sb.AppendLine("ExcludeFiles=" + Escape(cfg.ExcludeFiles));
            sb.AppendLine();
            sb.AppendLine("[Schedule]");
            sb.AppendLine("Enabled=" + (cfg.ScheduleEnabled ? "1" : "0"));
            sb.AppendLine("Time=" + cfg.ScheduleTime);
            sb.AppendLine();
            sb.AppendLine("[Folders]");
            sb.AppendLine("; index=include|source|subfolder");
            for (int i = 0; i < cfg.Folders.Count; i++)
            {
                var f = cfg.Folders[i];
                sb.AppendLine(i + "=" + (f.Include ? "1" : "0") + "|" + f.Source + "|" + f.SubFolder);
            }
            sb.AppendLine();
            sb.AppendLine("[AppList]");
            sb.AppendLine("Dest=" + cfg.AppListDest);
            File.WriteAllText(IniPath, sb.ToString());
        }

        static string Escape(string s) { return (s ?? "").Replace("\r\n", "\n").Replace("\n", "\\n"); }
        static string Unescape(string s) { return (s ?? "").Replace("\\n", "\r\n"); }

        // =====================================================================
        //  SCRIPT GENERATION  (guard-backup.cmd)
        // =====================================================================
        void WriteScript()
        {
            // Build the robocopy OPTS line from mode + excludes.
            string mirror = cfg.Mode == "Mirror" ? "/MIR" : "/E";
            string exDirs = ToOneLine(cfg.ExcludeDirs);
            string exFiles = ToOneLine(cfg.ExcludeFiles);
            var opts = new StringBuilder();
            // /XJ is MANDATORY: skips junction points. Without it, the hidden
            // My Music / My Pictures / My Videos junctions in Documents cause 3
            // failed-directory errors on every run.
            opts.Append(mirror).Append(" /R:2 /W:5 /MT:16 /NP /NFL /NDL /XJ");
            if (exDirs.Length > 0) opts.Append(" /XD ").Append(exDirs);
            if (exFiles.Length > 0) opts.Append(" /XF ").Append(exFiles);

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions");
            sb.AppendLine("REM ===========================================================================");
            sb.AppendLine("REM  guard-backup.cmd  -  GENERATED by GUARD.exe. Do not hand-edit;");
            sb.AppendLine("REM  your changes will be overwritten the next time you Save in the app.");
            sb.AppendLine("REM");
            sb.AppendLine("REM  USAGE:");
            sb.AppendLine("REM    guard-backup.cmd          run the backup for real (keeps window open)");
            sb.AppendLine("REM    guard-backup.cmd test     PREVIEW only - shows what WOULD change");
            sb.AppendLine("REM    guard-backup.cmd auto     silent (used by the scheduled task)");
            sb.AppendLine("REM ===========================================================================");
            sb.AppendLine();
            sb.AppendLine("set \"DEST=" + cfg.Dest + "\"");
            sb.AppendLine("set \"LOGDIR=%~dp0Logs\"");
            sb.AppendLine("set \"LOG=%LOGDIR%\\backup_last.log\"");
            sb.AppendLine();
            sb.AppendLine("set \"DRY=\"");
            sb.AppendLine("set \"PAUSEATEND=1\"");
            sb.AppendLine("if /I \"%~1\"==\"test\" set \"DRY=/L\"");
            sb.AppendLine("if /I \"%~1\"==\"auto\" set \"PAUSEATEND=\"");
            sb.AppendLine();
            sb.AppendLine("set \"OPTS=" + opts + "\"");
            sb.AppendLine();
            sb.AppendLine("if not exist \"%LOGDIR%\" md \"%LOGDIR%\"");
            sb.AppendLine();
            sb.AppendLine(">\"%LOG%\"  echo ===========================================================");
            sb.AppendLine(">>\"%LOG%\" echo  Backup     %date% %time%");
            sb.AppendLine(">>\"%LOG%\" echo  Destination: %DEST%");
            sb.AppendLine("if defined DRY >>\"%LOG%\" echo  *** PREVIEW MODE - no changes made ***");
            sb.AppendLine(">>\"%LOG%\" echo ===========================================================");
            sb.AppendLine();
            sb.AppendLine("echo.");
            sb.AppendLine("echo Backup    destination: %DEST%");
            sb.AppendLine("if defined DRY echo *** PREVIEW MODE - nothing will be copied or deleted ***");
            sb.AppendLine("echo Log file: %LOG%");
            sb.AppendLine("echo.");
            sb.AppendLine();
            sb.AppendLine("if not exist \"%DEST%\\\" (");
            sb.AppendLine("   echo ERROR: destination not reachable at %DEST%  - aborting.");
            sb.AppendLine("   >>\"%LOG%\" echo ERROR: destination not reachable at %DEST% - aborting.");
            sb.AppendLine("   goto :end");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("set \"HADERR=\"");
            sb.AppendLine();
            // @@PROGRESS@@ markers feed the app's progress bar. They are emitted
            // only when launched by the app (which sets GUARD_UI), so a manual
            // double-click of the .cmd does not show them. They go to stdout
            // only - never the log file.
            var inc = new List<FolderPair>();
            foreach (var f in cfg.Folders) if (f.Include) inc.Add(f);
            for (int i = 0; i < inc.Count; i++)
            {
                var f = inc[i];
                sb.AppendLine("if defined GUARD_UI echo @@PROGRESS@@ " + (i + 1) + " " + inc.Count + " " + MarkerSafe(f.SubFolder));
                sb.AppendLine("call :backup \"" + f.Source + "\" \"%DEST%\\" + f.SubFolder + "\"");
            }
            sb.AppendLine("if defined GUARD_UI echo @@PROGRESS@@ DONE");
            sb.AppendLine();
            sb.AppendLine(">>\"%LOG%\" echo.");
            sb.AppendLine("if defined HADERR (");
            sb.AppendLine("   >>\"%LOG%\" echo FINISHED WITH ERRORS   %date% %time%");
            sb.AppendLine("   echo.");
            sb.AppendLine("   echo Backup finished, but some folders reported errors - see the log.");
            sb.AppendLine(") else (");
            sb.AppendLine("   >>\"%LOG%\" echo FINISHED OK   %date% %time%");
            sb.AppendLine("   echo.");
            sb.AppendLine("   echo Backup finished successfully.");
            sb.AppendLine(")");
            sb.AppendLine("goto :end");
            sb.AppendLine();
            sb.AppendLine(":backup");
            sb.AppendLine("if not exist \"%~1\" (");
            sb.AppendLine("   echo SKIP  source not found: %~1");
            sb.AppendLine("   >>\"%LOG%\" echo.");
            sb.AppendLine("   >>\"%LOG%\" echo SKIP source not found: %~1");
            sb.AppendLine("   goto :eof");
            sb.AppendLine(")");
            sb.AppendLine("echo Backing up: %~1");
            sb.AppendLine(">>\"%LOG%\" echo.");
            sb.AppendLine(">>\"%LOG%\" echo --- %~1  =^> %~2");
            sb.AppendLine("robocopy \"%~1\" \"%~2\" %OPTS% %DRY% /LOG+:\"%LOG%\" /TEE");
            // Robocopy exit code >= 8 is a real error; 0-7 are success/info.
            sb.AppendLine("if errorlevel 8 (");
            sb.AppendLine("   set \"HADERR=1\"");
            sb.AppendLine("   echo    !! errors copying %~1 - see the log");
            sb.AppendLine(")");
            sb.AppendLine("goto :eof");
            sb.AppendLine();
            sb.AppendLine(":end");
            sb.AppendLine("endlocal");
            sb.AppendLine("echo.");
            sb.AppendLine("if defined PAUSEATEND pause");
            File.WriteAllText(ScriptPath, sb.ToString());
        }

        // Strip characters that would break a batch `echo` so a destination
        // subfolder name is safe to embed in an @@PROGRESS@@ marker line.
        static string MarkerSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append("&<>|^%\"".IndexOf(c) >= 0 ? ' ' : c);
            return sb.ToString().Trim();
        }

        static string ToOneLine(string multiline)
        {
            if (string.IsNullOrEmpty(multiline)) return "";
            var parts = multiline.Replace("\r\n", "\n").Split('\n');
            var keep = new List<string>();
            foreach (var p in parts) { var t = p.Trim(); if (t.Length > 0) keep.Add(t); }
            return string.Join(" ", keep.ToArray());
        }

        // =====================================================================
        //  SCHEDULED TASKS  (via PowerShell ScheduledTasks module)
        // =====================================================================
        void UpdateFileTask()
        {
            string arg = "/c \"" + ScriptPath + "\" auto";
            string ps =
                "$A = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '" + PsQuote(arg) + "';" +
                "$T = New-ScheduledTaskTrigger -Daily -At " + cfg.ScheduleTime + ";" +
                "$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" +
                "Register-ScheduledTask -TaskName '" + FileTaskName + "' -Action $A -Trigger $T -Settings $S -Force | Out-Null";
            RunPowerShell(ps, "Create/Update daily task");
            RefreshNextRun();
        }

        void RemoveTask(string name, Action after, bool silent = false, bool elevated = false)
        {
            string ps = "Unregister-ScheduledTask -TaskName '" + name + "' -Confirm:$false -ErrorAction SilentlyContinue";
            if (elevated) RunPowerShellElevated(ps, silent ? null : "Remove task"); else RunPowerShell(ps, null);
            if (after != null) after();
            if (!silent)
                MessageBox.Show("Removed scheduled task: " + name, "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void RefreshNextRun()
        {
            if (lblNextRun == null) return;
            string next = QueryNextRun(FileTaskName);
            lblNextRun.Content = next == null ? "Next run: (no scheduled task)" : "Next run: " + next;
        }

        string QueryNextRun(string name)
        {
            try
            {
                string ps = "try { (Get-ScheduledTaskInfo -TaskName '" + name + "' -ErrorAction Stop).NextRunTime.ToString('yyyy-MM-dd HH:mm') } catch { '' }";
                string outp = RunPowerShellCapture(ps).Trim();
                return string.IsNullOrEmpty(outp) ? null : outp;
            }
            catch { return null; }
        }

        static string PsQuote(string s) { return s.Replace("'", "''"); }

        void RunPowerShell(string script, string title)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 && title != null)
                        MessageBox.Show(title + " reported a problem:\r\n\r\n" + err, "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                if (title != null)
                    MessageBox.Show(title + " failed:\r\n\r\n" + ex.Message, "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        string RunPowerShellCapture(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return outp;
            }
        }

        // Run a PowerShell script ELEVATED (UAC prompt). Needed for registering or
        // removing a scheduled task that runs with highest privileges. Output
        // cannot be captured across the elevation boundary, so the script is
        // written to a temp .ps1 and its exit code is checked. Returns true on success.
        bool RunPowerShellElevated(string script, string title)
        {
            string ps1 = Path.Combine(Path.GetTempPath(), "guard_" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(ps1, script);
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        if (title != null)
                            MessageBox.Show(title + " did not complete (exit code " + p.ExitCode + ").",
                                "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (title != null)
                    MessageBox.Show(title + " was cancelled - Administrator approval was declined.",
                        "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            catch (Exception ex)
            {
                if (title != null)
                    MessageBox.Show(title + " failed:\r\n\r\n" + ex.Message,
                        "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                try { File.Delete(ps1); } catch { }
            }
        }

        // =====================================================================
        //  RUN A SCRIPT, STREAM OUTPUT
        // =====================================================================
        void RunScript(string script, string arg, TextBox outBox, ProgressBar bar, Label barLabel)
        {
            if (runningProc != null && !runningProc.HasExited)
            {
                MessageBox.Show("A backup is already running. Wait for it to finish.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Make sure the on-disk script reflects current settings (this is what
            // makes Run/Preview safe even before the first explicit Save Settings).
            // SaveAll returns false when the destination is blank; stop if so.
            if (!SaveAll()) return;
            if (!File.Exists(script))
            {
                MessageBox.Show("Script not found:\r\n" + script, "GUARD", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            outBox.Clear();
            AppendOut(outBox, "> " + Path.GetFileName(script) + (arg.Length > 0 ? " " + arg : "") + "\r\n");
            progTotal = 0;
            SetProgress(bar, barLabel, 1, 0, "");

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + script + "\" " + arg + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    WorkingDirectory = BaseDir
                };
                // GUARD_UI tells the script to emit @@PROGRESS@@ markers for the bar.
                psi.EnvironmentVariables["GUARD_UI"] = "1";
                runningProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                runningProc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { HandleScriptLine(outBox, bar, barLabel, e.Data); };
                runningProc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendOut(outBox, e.Data + "\r\n"); };
                runningProc.Exited += delegate { AppendOut(outBox, "\r\n--- finished ---\r\n"); };
                runningProc.Start();
                runningProc.BeginOutputReadLine();
                runningProc.BeginErrorReadLine();
                // Close stdin so the script's "pause" returns immediately instead of hanging.
                runningProc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                AppendOut(outBox, "ERROR launching script: " + ex.Message + "\r\n");
            }
        }

        // Intercept @@PROGRESS@@ marker lines to drive the progress bar; everything
        // else is appended to the output pane. Markers are consumed (not shown).
        void HandleScriptLine(TextBox outBox, ProgressBar bar, Label lbl, string data)
        {
            if (data == null) return;
            if (data.StartsWith("@@PROGRESS@@"))
            {
                string rest = data.Substring("@@PROGRESS@@".Length).Trim();
                if (rest == "DONE")
                {
                    SetProgress(bar, lbl, progTotal > 0 ? progTotal : 1, progTotal,
                        "Backup complete (" + progTotal + " of " + progTotal + ").");
                    return;
                }
                var m = Regex.Match(rest, "^(\\d+)\\s+(\\d+)\\s*(.*)$");
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[1].Value);
                    int tot = int.Parse(m.Groups[2].Value);
                    string nm = m.Groups[3].Value.Trim();
                    progTotal = tot;
                    // n = item starting (1-based); n-1 have completed.
                    SetProgress(bar, lbl, tot, n - 1, "Backing up: " + nm + " (" + n + " of " + tot + ")");
                }
                return;
            }
            AppendOut(outBox, data + "\r\n");
        }

        // Marshal a progress update to the UI thread.
        void SetProgress(ProgressBar bar, Label lbl, double max, double val, string text)
        {
            if (bar == null) return;
            if (!bar.Dispatcher.CheckAccess())
            {
                bar.Dispatcher.BeginInvoke((Action)delegate { SetProgress(bar, lbl, max, val, text); });
                return;
            }
            if (max > 0) bar.Maximum = max;
            bar.Value = val;
            if (lbl != null) lbl.Content = text;
        }

        void AppendOut(TextBox box, string text)
        {
            if (!box.Dispatcher.CheckAccess())
            {
                box.Dispatcher.BeginInvoke((Action)(() => AppendOut(box, text)));
                return;
            }
            box.AppendText(text);
            box.ScrollToEnd();
        }

        // =====================================================================
        //  SMALL HELPERS
        // =====================================================================
        void TestConnection(string path)
        {
            path = (path ?? "").Trim();
            if (path.Length == 0) { MessageBox.Show("Enter a destination path first.", "GUARD", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                if (Directory.Exists(path))
                {
                    MessageBox.Show("Reachable:\r\n" + path, "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // Try to create it (the parent may exist but the subfolder may not yet).
                Directory.CreateDirectory(path);
                MessageBox.Show("Created and reachable:\r\n" + path, "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Not reachable:\r\n" + path + "\r\n\r\n" + ex.Message, "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void OpenPath(string path)
        {
            try
            {
                path = (path ?? "").Trim();
                if (File.Exists(path) || Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    MessageBox.Show("Not found:\r\n" + path, "GUARD", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open:\r\n" + path + "\r\n\r\n" + ex.Message, "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Open a folder picker and drop the chosen path into the given box. The
        // box stays editable, so typed or pasted paths still work.
        void BrowseInto(TextBox box)
        {
            using (var fb = new WinForms.FolderBrowserDialog())
            {
                var cur = (box.Text ?? "").Trim();
                if (cur.Length > 0)
                {
                    try { fb.SelectedPath = Environment.ExpandEnvironmentVariables(cur); }
                    catch { }
                }
                if (fb.ShowDialog() == WinForms.DialogResult.OK) box.Text = fb.SelectedPath;
            }
        }

        // Help opens the bundled README.md; if it is not next to the exe (for
        // example a stand-alone copy of just the .exe), fall back to the project
        // page online.
        void OpenHelp()
        {
            string readme = Path.Combine(BaseDir, "README.md");
            if (File.Exists(readme)) { OpenPath(readme); return; }
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open help:\r\n\r\n" + ex.Message, "GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void ShowAbout()
        {
            var w = new AboutWindow { Owner = this };
            w.ShowDialog();
        }

        // Validate "HH:mm" (24h). On bad input keep the previous value so the
        // generated task/script never gets a malformed time.
        static string NormalizeTime(string text, string fallback)
        {
            text = (text ?? "").Trim();
            DateTime t;
            if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return t.ToString("HH:mm");
            if (DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return t.ToString("HH:mm");
            return fallback;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if ((runningProc != null && !runningProc.HasExited) || reinstalling)
            {
                string what = reinstalling ? "An app reinstall is still running." : "A backup is still running.";
                if (MessageBox.Show(what + " Close anyway?", "GUARD",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                { e.Cancel = true; return; }
            }
            base.OnClosing(e);
        }
    }

    // ----- dialog to add a folder pair --------------------------------------
    class FolderWindow : Window
    {
        TextBox txtSource, txtSub;
        public string SourcePath { get { return txtSource.Text.Trim(); } }
        public string SubFolder { get { return txtSub.Text.Trim(); } }

        public FolderWindow(FolderPair existing)
        {
            Title = "Add Folder";
            Width = 560; Height = 210;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty, "WindowBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");

            var root = new StackPanel { Margin = new Thickness(14) };

            // Source row
            txtSource = new TextBox { Width = 320 };
            AutomationProperties.SetName(txtSource, "Source folder path");
            var btnBrowse = new Button { Content = "_Browse...", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(btnBrowse, "Browse for source folder");
            btnBrowse.Click += delegate
            {
                using (var fb = new WinForms.FolderBrowserDialog())
                    if (fb.ShowDialog() == WinForms.DialogResult.OK) txtSource.Text = fb.SelectedPath;
            };
            var srcRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            srcRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            srcRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            srcRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lblSrc = new Label { Content = "_Source folder:", Target = txtSource, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lblSrc, 0); Grid.SetColumn(txtSource, 1); Grid.SetColumn(btnBrowse, 2);
            txtSource.VerticalAlignment = VerticalAlignment.Center;
            srcRow.Children.Add(lblSrc); srcRow.Children.Add(txtSource); srcRow.Children.Add(btnBrowse);
            root.Children.Add(srcRow);

            // Subfolder row
            txtSub = new TextBox { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(txtSub, "Subfolder name");
            var subRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblSub = new Label { Content = "Su_bfolder:", Target = txtSub, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lblSub, 0); Grid.SetColumn(txtSub, 1);
            subRow.Children.Add(lblSub); subRow.Children.Add(txtSub);
            root.Children.Add(subRow);

            var hint = new TextBlock { Text = "The subfolder is the name used under the backup destination root.", Margin = new Thickness(0, 0, 0, 12) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            root.Children.Add(hint);

            // Buttons
            var btnOk = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnOk.Click += delegate
            {
                if (SourcePath.Length == 0 || SubFolder.Length == 0)
                {
                    MessageBox.Show("Fill in both the source folder and the subfolder.", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DialogResult = true;
            };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnRow.Children.Add(btnOk); btnRow.Children.Add(btnCancel);
            root.Children.Add(btnRow);

            if (existing != null) { txtSource.Text = existing.Source; txtSub.Text = existing.SubFolder; }

            Content = root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyTitleBar(this);
        }
    }

    // ----- About dialog ------------------------------------------------------
    class AboutWindow : Window
    {
        public AboutWindow()
        {
            Title = "About GUARD";
            Width = 440; Height = 300;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty, "WindowBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");

            var root = new StackPanel { Margin = new Thickness(18) };

            var heading = new TextBlock { Text = "GUARD", FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
            AutomationProperties.SetName(heading, "GUARD");
            root.Children.Add(heading);
            root.Children.Add(new TextBlock { Text = "Version " + MainWindow.AppVersion, Margin = new Thickness(0, 0, 0, 12) });
            root.Children.Add(new TextBlock { Text = "A backup and app inventory utility for Windows.", Margin = new Thickness(0, 0, 0, 10) });
            root.Children.Add(new TextBlock { Text = "Created by PlanetLinux98.", Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = "Released under the MIT License.", Margin = new Thickness(0, 0, 0, 14) });

            var btnRepo = new Button { Content = "_Project Page", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetName(btnRepo, "Open the GUARD project page in your browser");
            btnRepo.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(MainWindow.RepoUrl) { UseShellExecute = true }); } catch { }
            };
            root.Children.Add(btnRepo);

            var btnClose = new Button { Content = "_Close", MinWidth = 90, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
            btnClose.Click += delegate { Close(); };
            root.Children.Add(btnClose);

            Content = root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyTitleBar(this);
        }
    }
}
