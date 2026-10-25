using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SefarimEditor
{
    internal static class Program
    {
        public const string EditorMutexName = @"Local\SefarimEditor.Editor";
        public const string ShowEventName = @"Local\SefarimEditor.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            NativeLoader.Ensure();
            // תהליך הרקע רץ בלי לטעון את WPF, כדי לצרוך כמה שפחות זיכרון.
            if (args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))) return RunTray();
            return RunEditor(args);
        }

        /// <summary>בדיקה עצמית לאבחון: פותח את הספריות שנמצאו לקריאה וכותב את התוצאה לקובץ.</summary>
        private static int SelfCheck(string outFile)
        {
            var report = new System.Text.StringBuilder();
            foreach (var kind in new[] { Sefarim.Core.LibraryKind.Otzaria, Sefarim.Core.LibraryKind.Zayit })
            {
                var path = Sefarim.Core.LibraryLocator.Find(kind);
                report.AppendLine($"{kind}: {path ?? "לא נמצא"}");
                if (path == null) continue;
                try
                {
                    using (var db = Sefarim.Core.SeforimDb.Open(path, readOnly: true))
                        report.AppendLine($"  ספרים: {db.LoadCatalog().Books.Count}, גרסה: {db.DbVersion}");
                }
                catch (Exception ex)
                {
                    report.AppendLine("  שגיאה: " + ex);
                }
            }
            System.IO.File.WriteAllText(outFile, report.ToString(), System.Text.Encoding.UTF8);
            return 0;
        }

        private static string Arg(string[] args, string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>
        /// מצב בדיקה פנימי: מצייר את החלון מחוץ למסך לקובץ תמונה ויוצא, בלי להקפיץ חלון.
        /// --snapshot קובץ [--tab מספר] [--category מזהה] [--book מזהה] [--search טקסט]
        /// </summary>
        private static int RunSnapshot(string[] args, string path)
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SefarimEditor;component/Theme.xaml"),
            });
            var window = new MainWindow
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -30000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            long.TryParse(Arg(args, "--category"), out var cat);
            long.TryParse(Arg(args, "--book"), out var book);
            int.TryParse(Arg(args, "--tab"), out var tab);
            window.Loaded += async (s, e) =>
                await window.Snapshot(path, tab, cat == 0 ? (long?)null : cat, book == 0 ? (long?)null : book, Arg(args, "--search"));
            return app.Run(window);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunTray() => TrayApp.Run();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunEditor(string[] args)
        {
            var check = Arg(args, "--selfcheck");
            if (check != null) return SelfCheck(check);
            var snapshot = Arg(args, "--snapshot");
            if (snapshot != null) return RunSnapshot(args, snapshot);
            using (var mutex = new Mutex(true, EditorMutexName, out var created))
            {
                if (!created)
                {
                    // העורך כבר פתוח: מבקשים ממנו לקפוץ לחזית.
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }

                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/SefarimEditor;component/Theme.xaml"),
                });
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Dialogs.Error(app.MainWindow, "אירעה שגיאה", e.Exception.Message);
                    e.Handled = true;
                };

                var window = new MainWindow();
                var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                var listener = new Thread(() =>
                {
                    while (show.WaitOne())
                    {
                        window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
                            window.Activate();
                            window.Topmost = true;
                            window.Topmost = false;
                        }));
                    }
                }) { IsBackground = true };
                listener.Start();
                return app.Run(window);
            }
        }
    }
}
