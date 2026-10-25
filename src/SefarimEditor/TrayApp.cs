using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sefarim.Core;

namespace SefarimEditor
{
    /// <summary>
    /// תהליך הרקע: אייקון ליד השעון שבודק אם יצאה גרסת ספרייה חדשה, מחזיר את הקובץ
    /// למקור לפני עדכון (באישור המשתמש), ומחיל את השינויים שוב אחרי העדכון.
    /// כתוב ב-WinForms בלבד כדי לצרוך מעט זיכרון.
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        public static int Run()
        {
            using (var mutex = new Mutex(true, TrayLauncher.MutexName, out var created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Autostart.RefreshPathIfEnabled(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
                Application.Run(new TrayApp());
                return 0;
            }
        }

        private enum Trigger
        {
            Startup,
            Periodic,
            AppStarted,
            AppClosed,
            ReleaseChecked,
            DriveArrived,
            Manual,
        }

        private readonly NotifyIcon _icon;
        private readonly Control _ui = new Control();
        private readonly System.Windows.Forms.Timer _processTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private readonly System.Windows.Forms.Timer _slowTimer = new System.Windows.Forms.Timer { Interval = Fast ? 8000 : 15 * 60 * 1000 };
        private readonly Dictionary<LibraryKind, bool> _running = new Dictionary<LibraryKind, bool>();
        private readonly HashSet<string> _warnedThisRun = new HashSet<string>();
        private readonly DriveWatcher _drives;
        private bool _busy;
        private bool _promptOpen;

        private TrayApp()
        {
            var h = _ui.Handle; // יוצר את החלון הנסתר על התהליכון הראשי, להחזרת עבודה מרקע.
            _icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName),
                Text = "עורך הספרייה – תהליך רקע",
                Visible = true,
                ContextMenuStrip = BuildMenu(),
            };
            _icon.DoubleClick += (s, e) => OpenEditor();
            _icon.BalloonTipClicked += (s, e) => OpenEditor();

            foreach (var k in new[] { LibraryKind.Otzaria, LibraryKind.Zayit }) _running[k] = LibraryLocator.IsRunning(k);

            _processTimer.Tick += (s, e) => CheckProcesses();
            _processTimer.Start();
            _slowTimer.Tick += (s, e) => { MaybeCheckReleases(force: false); EvaluateAll(Trigger.Periodic); TrimMemory(); };
            _slowTimer.Start();
            _drives = new DriveWatcher(() => EvaluateAll(Trigger.DriveArrived));

            var startup = new System.Windows.Forms.Timer { Interval = Fast ? 1500 : 20000 };
            startup.Tick += (s, e) =>
            {
                startup.Stop();
                startup.Dispose();
                MaybeCheckReleases(force: false);
                EvaluateAll(Trigger.Startup);
                TrimMemory();
            };
            startup.Start();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        /// <summary>מחזיר ל-Windows זיכרון שלא בשימוש, כדי שהתהליך ייקח כמה שפחות בזמן ההמתנה.</summary>
        private static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using (var me = Process.GetCurrentProcess()) SetProcessWorkingSetSize(me.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, Font = new Font("Segoe UI", 10f) };
            var open = new ToolStripMenuItem("פתח את עורך הספרייה", null, (s, e) => OpenEditor()) { Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripMenuItem("בדוק עדכוני ספרייה עכשיו", null, (s, e) =>
            {
                Balloon("בודק…", "בודק אם יצאה גרסת ספרייה חדשה.");
                MaybeCheckReleases(force: true, report: true);
            }));
            var snooze = new ToolStripMenuItem("השהה הודעות לשבוע");
            snooze.Click += (s, e) =>
            {
                var st = AppSettings.Load();
                var snoozed = st.Libraries.Any(l => l.SnoozeUntil > DateTime.Now);
                foreach (var l in st.Libraries) l.SnoozeUntil = snoozed ? (DateTime?)null : DateTime.Now.AddDays(7);
                st.Save();
                Balloon("עורך הספרייה", snoozed ? "ההודעות חודשו." : "לא יוצגו הודעות על עדכונים במשך שבוע.");
            };
            menu.Opening += (s, e) =>
                snooze.Text = AppSettings.Load().Libraries.Any(l => l.SnoozeUntil > DateTime.Now) ? "חדש הודעות" : "השהה הודעות לשבוע";
            menu.Items.Add(snooze);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("יציאה מתהליך הרקע", null, (s, e) => ExitThread()));
            return menu;
        }

        protected override void ExitThreadCore()
        {
            _processTimer.Stop();
            _slowTimer.Stop();
            _drives.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }

        private static void OpenEditor()
        {
            try { Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = false }); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            Log($"הודעה: {title} | {text}");
            _icon.ShowBalloonTip(8000, title, text, icon);
        }

        private static readonly bool Fast = Environment.GetEnvironmentVariable("SEFARIM_TEST_FAST") == "1";

        /// <summary>יומן קצר של תהליך הרקע, בתיקיית ההגדרות (לאבחון תקלות).</summary>
        private static void Log(string line)
        {
            try
            {
                var path = Path.Combine(AppSettings.Folder, "tray.log");
                Directory.CreateDirectory(AppSettings.Folder);
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}", System.Text.Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ───────────────────────── מעקב אחרי התוכנות ─────────────────────────

        private void CheckProcesses()
        {
            foreach (var kind in _running.Keys.ToList())
            {
                var now = LibraryLocator.IsRunning(kind);
                if (now == _running[kind]) continue;
                _running[kind] = now;
                Evaluate(kind, now ? Trigger.AppStarted : Trigger.AppClosed);
            }
        }

        private void EvaluateAll(Trigger trigger)
        {
            foreach (var kind in new[] { LibraryKind.Otzaria, LibraryKind.Zayit }) Evaluate(kind, trigger);
        }

        private sealed class LibState
        {
            public LibraryConfig Cfg;
            public string Path;
            public string Root;
            public long? Version;
            public LibraryStatus Status;
            public long? RestoredAt;
            public bool Replaced;
            public bool StoreKnown;
        }

        private static LibState Read(LibraryConfig cfg)
        {
            var path = cfg.ResolveDbPath();
            if (path == null) return null;
            var st = new LibState { Cfg = cfg, Path = path, Root = cfg.ResolveBackupRoot() };
            st.Version = LibraryLocator.ReadVersionQuick(cfg.Kind, path);
            if (st.Root != null)
            {
                var storePath = BackupStore.PathFor(st.Root, cfg.Kind, path);
                var (ops, restoredAt) = File.Exists(storePath) ? BackupStore.ReadFile(storePath) : (new List<AppliedOp>(), null);
                st.StoreKnown = true;
                st.Status = LibrarySession.StatusOf(ops, st.Version);
                st.RestoredAt = restoredAt;
                st.Replaced = ops.Any(o => o.State == OpState.Applied && o.DbVersion != st.Version);
            }
            else
            {
                st.Status = cfg.LastStatus;
                if (cfg.LastStatus == LibraryStatus.Edited && cfg.LastDbVersion != st.Version)
                {
                    st.Status = LibraryStatus.WaitingReapply;
                    st.Replaced = true;
                }
            }
            return st;
        }

        private void Evaluate(LibraryKind kind, Trigger trigger)
        {
            if (_busy || _promptOpen) return;
            LibState st;
            try
            {
                var cfg = AppSettings.Load().Libraries.FirstOrDefault(l => l.Kind == kind);
                if (cfg == null || !cfg.Watch) return;
                st = Read(cfg);
                if (st == null) return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return;
            }

            var name = LibraryLocator.DisplayName(kind);
            var running = _running.TryGetValue(kind, out var r) && r;
            Log($"{kind}: {trigger}, מצב {st.Status}, גרסה {st.Version}, הוחזר בגרסה {st.RestoredAt}, פתוחה {running}, גיבוי {(st.Root == null ? "לא זמין" : "זמין")}");
            var snoozed = st.Cfg.SnoozeUntil > DateTime.Now;

            switch (st.Status)
            {
                case LibraryStatus.Edited:
                {
                    // רק באוצריא יש עדכון חלקי שכדאי להכין אליו. זית מחליפה את כל הקובץ.
                    var latest = AppSettings.Load().LatestOtzariaRelease;
                    if (kind != LibraryKind.Otzaria || latest == null || st.Version == null || latest <= st.Version) return;
                    if (running)
                    {
                        if (trigger == Trigger.AppStarted && _warnedThisRun.Add(kind + ":" + latest) && !snoozed)
                            Balloon($"{name} נפתחה, ויש עדכון ספרייה",
                                $"הקובץ עדיין ערוך. אם {name} מציעה הורדה מלאה, אפשר לסרב. כשתסגור אותה, אציע להכין את הקובץ לעדכון.", ToolTipIcon.Warning);
                        return;
                    }
                    if (snoozed || st.Cfg.NotifiedReleaseVersion == latest) return;
                    if (trigger == Trigger.Periodic && st.Root == null) return;
                    PromptPrepare(st, latest.Value);
                    return;
                }
                case LibraryStatus.WaitingReapply:
                {
                    if (running) return;
                    var updated = st.Replaced || (st.RestoredAt != null && st.Version != null && st.Version > st.RestoredAt);
                    if (updated)
                    {
                        if (st.Root == null)
                        {
                            if (_warnedThisRun.Add(kind + ":card:" + st.Version))
                                Balloon("חבר את כרטיס הגיבוי", $"הספרייה של {name} עודכנה. כדי להחיל שוב את השינויים שלך, חבר את התיקייה {st.Cfg.BackupRoot}.", ToolTipIcon.Warning);
                            return;
                        }
                        Reapply(st);
                        return;
                    }
                    if (trigger == Trigger.AppClosed && st.Root != null && !snoozed)
                    {
                        var choice = Prompt($"הספרייה של {name} עדיין לא עודכנה",
                            $"הקובץ הוחזר למקור לקראת עדכון, אבל הגרסה לא השתנתה. אם {name} לא הציעה עדכון (למשל כי אין אינטרנט), אפשר להחיל את השינויים שלך שוב עכשיו, ולהכין את הקובץ לעדכון בפעם אחרת.",
                            "החל את השינויים עכשיו", "המתן לעדכון");
                        if (choice == 0) Reapply(st);
                    }
                    return;
                }
            }
        }

        // ───────────────────────── פעולות ─────────────────────────

        private void PromptPrepare(LibState st, long latest)
        {
            var name = LibraryLocator.DisplayName(st.Cfg.Kind);
            if (st.Root == null)
            {
                if (!_warnedThisRun.Add(st.Cfg.Kind + ":needcard:" + latest)) return;
                var c = Prompt($"יש עדכון לספריית {name}",
                    $"יצאה גרסת ספרייה חדשה ({latest}). כדי ש{name} תוריד רק את ההפרשים, צריך להחזיר קודם את הקובץ למקור, ולשם כך צריך את תיקיית הגיבוי ({st.Cfg.BackupRoot}).\n\nחבר את הכרטיס, ואציע שוב מיד.",
                    "בסדר", "הזכר לי מחר", "דלג על הגרסה הזו");
                HandleLater(st, latest, c);
                return;
            }
            var choice = Prompt($"יש עדכון לספריית {name}",
                $"יצאה גרסת ספרייה חדשה ({latest}). כדי ש{name} תוריד רק את ההפרשים, צריך להחזיר קודם את הקובץ למקור.\n\n" +
                "השינויים שלך לא נמחקים: הם שמורים בגיבוי, ואחרי העדכון הם יוחלו שוב אוטומטית.",
                "הכן לעדכון עכשיו", "הזכר לי מחר", "דלג על הגרסה הזו");
            if (choice == 0) RestoreForUpdate(st);
            else HandleLater(st, latest, choice);
        }

        private static void HandleLater(LibState st, long latest, int choice)
        {
            if (choice != 1 && choice != 2) return;
            var settings = AppSettings.Load();
            var cfg = settings.Get(st.Cfg.Kind, st.Cfg.DbPath);
            if (choice == 1) cfg.SnoozeUntil = DateTime.Now.AddDays(1);
            else cfg.NotifiedReleaseVersion = latest;
            settings.Save();
        }

        private void RestoreForUpdate(LibState st)
        {
            var name = LibraryLocator.DisplayName(st.Cfg.Kind);
            RunInBackground($"מכין את {name} לעדכון", "מחזיר את הקובץ למקור…", s =>
            {
                s.RestoreAll();
                return $"הקובץ חזר למקור. פתח את {name} כדי לעדכן. אחרי שתסגור אותה, השינויים שלך יוחלו שוב.";
            }, st);
        }

        private void Reapply(LibState st)
        {
            var name = LibraryLocator.DisplayName(st.Cfg.Kind);
            RunInBackground("מחיל את השינויים שלך שוב", $"הספרייה של {name} עודכנה. מחיל שוב את השינויים…", s =>
            {
                var res = s.ReapplyRestored();
                if (res.Problems.Count > 0) return $"חלק מהשינויים לא הוחלו ({res.Problems.Count}). פתח את העורך לפרטים.";
                if (res.Applied == 0 && res.Notes.Count == 0 && s.ReplacedOps == 0) return "השינויים שלך עדיין נמצאים בקובץ, ולא היה צורך להחיל אותם שוב.";
                return $"הוחלו {res.Applied} שינויים." + (res.Notes.Count > 0 ? " " + res.Notes[0] : "");
            }, st);
        }

        private void RunInBackground(string title, string startText, Func<LibrarySession, string> work, LibState st)
        {
            if (_busy) return;
            _busy = true;
            Balloon(title, startText);
            var kind = st.Cfg.Kind;
            var path = st.Path;
            var root = st.Root;
            Task.Run(() =>
            {
                try
                {
                    string message;
                    var settings = AppSettings.Load();
                    var cfg = settings.Get(kind, st.Cfg.DbPath);
                    using (var s = LibrarySession.OpenForWrite(kind, path, root))
                    {
                        message = work(s);
                        settings.Mirror(cfg, s);
                    }
                    settings.Save();
                    return (ok: true, message);
                }
                catch (Exception ex)
                {
                    return (ok: false, message: ex.Message);
                }
            }).ContinueWith(t =>
            {
                _ui.BeginInvoke(new Action(() =>
                {
                    _busy = false;
                    Balloon(t.Result.ok ? title : "הפעולה לא בוצעה", t.Result.message, t.Result.ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                    TrimMemory();
                }));
            });
        }

        private void MaybeCheckReleases(bool force, bool report = false)
        {
            var settings = AppSettings.Load();
            // בודקים רק כשיש בכלל ספריית אוצריא ערוכה שעוקבים אחריה.
            var relevant = settings.Libraries.Any(l => l.Kind == LibraryKind.Otzaria && l.Watch && l.LastStatus == LibraryStatus.Edited);
            if (!force && !relevant) return;
            if (!force && settings.LastReleaseCheck > DateTime.Now.AddHours(-Math.Max(1, settings.CheckHours))) return;
            Task.Run(() => ReleaseChecker.LatestOtzaria()).ContinueWith(t =>
            {
                _ui.BeginInvoke(new Action(() =>
                {
                    var latest = t.Result;
                    var st = AppSettings.Load();
                    if (latest != null)
                    {
                        st.LatestOtzariaRelease = latest;
                        st.LastReleaseCheck = DateTime.Now;
                        st.Save();
                    }
                    if (report)
                    {
                        var cfg = st.Libraries.FirstOrDefault(l => l.Kind == LibraryKind.Otzaria);
                        var local = cfg?.ResolveDbPath() is string p ? LibraryLocator.ReadVersionQuick(LibraryKind.Otzaria, p) : null;
                        Balloon("בדיקת עדכונים", latest == null ? "הבדיקה נכשלה (אין חיבור לאינטרנט?)."
                            : latest > local ? $"יש גרסת ספרייה חדשה לאוצריא: {latest} (אצלך {local})."
                            : $"ספריית אוצריא מעודכנת (גרסה {local}).");
                        if (cfg != null && latest > local) { cfg.NotifiedReleaseVersion = null; cfg.SnoozeUntil = null; st.Save(); }
                    }
                    EvaluateAll(report ? Trigger.Manual : Trigger.ReleaseChecked);
                }));
            });
        }

        // ───────────────────────── חלון שאלה ─────────────────────────

        /// <summary>מציג שאלה בעברית ומחזיר את מספר הכפתור שנלחץ (0 = הראשון), או -1 בסגירה.</summary>
        private int Prompt(string title, string body, params string[] buttons)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("SEFARIM_TEST_ANSWER"), out var auto))
            {
                Log($"שאלה: {title} → תשובה אוטומטית {auto}");
                return auto;
            }
            _promptOpen = true;
            try
            {
                using (var form = new Form
                       {
                           Text = "עורך הספרייה",
                           RightToLeft = RightToLeft.Yes,
                           RightToLeftLayout = true,
                           FormBorderStyle = FormBorderStyle.FixedDialog,
                           StartPosition = FormStartPosition.CenterScreen,
                           MaximizeBox = false,
                           MinimizeBox = false,
                           TopMost = true,
                           ShowInTaskbar = true,
                           Font = new Font("Segoe UI", 10.5f),
                           BackColor = Color.White,
                           AutoScaleMode = AutoScaleMode.Font,
                           Icon = _icon.Icon,
                       })
                {
                    var result = -1;
                    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 16), ColumnCount = 1, RowCount = 3, AutoSize = true };
                    layout.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 0, 0, 10) });
                    layout.Controls.Add(new Label { Text = body, AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 0, 0, 16) });
                    var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var index = i;
                        var b = new Button
                        {
                            Text = buttons[i],
                            AutoSize = true,
                            Padding = new Padding(10, 4, 10, 4),
                            Margin = new Padding(0, 0, 8, 0),
                            FlatStyle = FlatStyle.System,
                        };
                        b.Click += (s, e) => { result = index; form.Close(); };
                        row.Controls.Add(b);
                        if (i == 0) form.AcceptButton = b;
                    }
                    layout.Controls.Add(row);
                    form.Controls.Add(layout);
                    form.AutoSize = true;
                    form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    form.ShowDialog();
                    return result;
                }
            }
            finally
            {
                _promptOpen = false;
            }
        }

        /// <summary>מקשיב להודעת Windows על חיבור כונן (למשל כרטיס זיכרון), בלי לבדוק שוב ושוב.</summary>
        private sealed class DriveWatcher : NativeWindow, IDisposable
        {
            private const int WM_DEVICECHANGE = 0x0219;
            private const int DBT_DEVICEARRIVAL = 0x8000;
            private readonly Action _onArrival;
            private readonly System.Windows.Forms.Timer _debounce = new System.Windows.Forms.Timer { Interval = 3000 };

            public DriveWatcher(Action onArrival)
            {
                _onArrival = onArrival;
                _debounce.Tick += (s, e) => { _debounce.Stop(); _onArrival(); };
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt64() == DBT_DEVICEARRIVAL)
                {
                    _debounce.Stop();
                    _debounce.Start();
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                _debounce.Dispose();
                DestroyHandle();
            }
        }
    }
}
