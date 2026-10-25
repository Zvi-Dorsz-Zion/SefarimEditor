using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace SefarimEditor
{
    /// <summary>הפעלת תהליך הרקע עם Windows, דרך מפתח Run של המשתמש הנוכחי (בלי הרשאות מנהל).</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SefarimEditor";

        private static string Command => "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key?.GetValue(ValueName) is string;
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>מעדכן את הנתיב אם התוכנה הועברה למקום אחר.</summary>
        public static void RefreshPathIfEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                if (key?.GetValue(ValueName) is string current && current != Command) key.SetValue(ValueName, Command);
            }
        }
    }

    internal static class TrayLauncher
    {
        public const string MutexName = @"Local\SefarimEditor.Tray";

        public static bool IsRunning()
        {
            if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
            m.Dispose();
            return true;
        }

        public static void Start() =>
            Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--tray") { UseShellExecute = false });
    }
}
