using System;
using System.Threading;

namespace GuardWui3.Services;

// Windows toast notifications for unattended runs (the scheduled and
// on-connect backups). Raised from the headless helper, never from the open
// app - the app announces through the screen reader instead. Unpackaged apps
// must register an identity before showing (AppNotificationManager writes an
// HKCU AppUserModelId entry, GUARD's one registry footprint besides the
// scheduled tasks); registration happens only when a toast is actually being
// shown, so users with notifications off never get the registry entry.
public static class ToastNotifier
{
    // Blocking on purpose: the helper process exits right after this, and a
    // toast handed to the pipeline too close to process exit is dropped. A
    // dedicated MTA thread keeps WinRT activation off the STA main thread.
    // IsBackground stays true so a notification pipeline that hangs past the
    // 15s Join cannot keep the whole headless process alive after Show
    // returns - a foreground thread would block process exit indefinitely.
    public static void Show(string title, string body)
    {
        var th = new Thread(() =>
        {
            try { ShowCore(title, body); }
            catch (Exception ex) { DebugLog.Log("toast", "could not show notification", ex); }
        });
        th.SetApartmentState(ApartmentState.MTA);
        th.IsBackground = true;
        th.Start();
        th.Join(TimeSpan.FromSeconds(15));
    }

    private static void ShowCore(string title, string body)
    {
        var mgr = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
        mgr.Register();
        string xml = "<toast><visual><binding template=\"ToastGeneric\">"
            + "<text>" + XmlEscape(title) + "</text>"
            + "<text>" + XmlEscape(body) + "</text>"
            + "</binding></visual></toast>";
        mgr.Show(new Microsoft.Windows.AppNotifications.AppNotification(xml));
        // Give the notification pipeline time to take delivery before exit.
        Thread.Sleep(3000);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
