using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sefarim.Core;

namespace SefarimEditor
{
    public partial class MainWindow : Window
    {
        private sealed class LibraryChoice
        {
            public LibraryKind Kind { get; set; }
            public string Path { get; set; }
            public string Display { get; set; }
        }

        private sealed class OpRow
        {
            public string Summary { get; set; }
            public string Details { get; set; }
            public string StateText { get; set; }
            public Brush StateBrush { get; set; }
        }

        private readonly AppSettings _settings = AppSettings.Load();
        private LibraryConfig _cfg;
        private LibraryChoice _choice;
        private SeforimDb _ro;
        private Catalog _catalog;
        private List<AppliedOp> _ops = new List<AppliedOp>();
        private LibraryStatus _status;
        private bool _storeAvailable;
        private bool _suggestCompact;

        private BookInfo _book;
        private BookDetails _details;
        private CategoryNode _pendingMove;
        private CategoryNode _category;
        private bool _filling;

        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        private readonly DispatcherTimer _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private bool _lastRunning;

        public MainWindow()
        {
            InitializeComponent();
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplySearch(); };
            _runningTimer.Tick += (s, e) =>
            {
                if (_choice == null) return;
                var running = LibraryLocator.IsRunning(_choice.Kind);
                if (running != _lastRunning) RefreshStatus();
            };
            foreach (var h in new[] { 1, 3, 6, 12, 24 }) CheckHours.Items.Add(h);
            CheckHours.SelectedItem = _settings.CheckHours;
            StartWithWindows.IsChecked = Autostart.IsEnabled();
            RichText.Set(AboutText, $"עורך הספרייה, גרסה {typeof(MainWindow).Assembly.GetName().Version.ToString(3)}\n" +
                                    $"ההגדרות נשמרות ב: {Ltr(AppSettings.Folder)}");
            Loaded += async (s, e) =>
            {
                await InitLibraries();
                // אם תהליך הרקע הוגדר לעלות עם Windows אבל לא רץ כרגע, מפעילים אותו.
                try
                {
                    if (Autostart.IsEnabled() && !TrayLauncher.IsRunning()) TrayLauncher.Start();
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            };
            Closed += (s, e) => _ro?.Dispose();
            _runningTimer.Start();
        }

        /// <summary>מצב בדיקה: מכין מצב תצוגה, מצייר את החלון לקובץ PNG ונסגר.</summary>
        internal async Task Snapshot(string path, int tab, long? category, long? book, string search)
        {
            for (var i = 0; i < 100 && _catalog == null; i++) await Task.Delay(200);
            Tabs.SelectedIndex = tab;
            if (_catalog != null)
            {
                if (category.HasValue && _catalog.Categories.TryGetValue(category.Value, out var c))
                {
                    ShowCategoryBooks(c);
                    ShowCategory(c);
                }
                if (!string.IsNullOrEmpty(search))
                {
                    SearchBox.Text = search;
                    ApplySearch();
                }
                if (book.HasValue && _catalog.Books.TryGetValue(book.Value, out var b))
                {
                    if (BookList.ItemsSource == null) BookList.ItemsSource = new List<BookInfo> { b };
                    BookList.SelectedItem = b;
                    ShowBook(b);
                    PreviewExpander.IsExpanded = true;
                }
            }
            await Task.Delay(1200);
            var root = (FrameworkElement)Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            // היפוך המראה של החלון (מימין לשמאל) נעשה ברמת החלון, ולכן ציור התוכן לבדו יוצא הפוך.
            var flipped = new System.Windows.Media.Imaging.TransformedBitmap(rtb, new ScaleTransform(-1, 1));
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(flipped));
            using (var fs = File.Create(path)) enc.Save(fs);
            Close();
        }

        // ───────────────────────── טעינה ─────────────────────────

        private async Task InitLibraries()
        {
            var choices = new List<LibraryChoice>();
            foreach (var kind in new[] { LibraryKind.Otzaria, LibraryKind.Zayit })
            {
                var path = _settings.Libraries.FirstOrDefault(l => l.Kind == kind)?.ResolveDbPath() ?? LibraryLocator.Find(kind);
                if (path != null) choices.Add(new LibraryChoice { Kind = kind, Path = path, Display = LibraryLocator.DisplayName(kind) });
            }
            foreach (var other in _settings.Libraries.Where(l => l.Kind == LibraryKind.Other && File.Exists(l.DbPath)))
                choices.Add(new LibraryChoice { Kind = LibraryKind.Other, Path = other.DbPath, Display = other.DisplayName });

            LibraryCombo.ItemsSource = choices;
            if (choices.Count == 0)
            {
                ShowBanner("לא נמצאו במחשב ספריות של אוצריא או של זית. אפשר לפתוח קובץ seforim.db ידנית.", "פתח קובץ…", () => OpenOther_Click(null, null));
                return;
            }
            var last = choices.FirstOrDefault(c => c.Kind == _settings.LastOpened) ?? choices[0];
            LibraryCombo.SelectedItem = last;
            await Task.CompletedTask;
        }

        private async void LibraryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LibraryCombo.SelectedItem is LibraryChoice c) await LoadLibrary(c);
        }

        private async Task LoadLibrary(LibraryChoice choice)
        {
            _choice = choice;
            _cfg = _settings.Get(choice.Kind, choice.Path);
            _settings.LastOpened = choice.Kind;
            SaveSettings();
            ClearDetails();
            BookList.ItemsSource = null;
            await Reload("טוען את הספרייה…");
            WatchThisLibrary.IsChecked = _cfg.Watch;
            BackupPath.Text = _cfg.BackupRoot ?? "";
        }

        /// <summary>טוען מחדש את הקטלוג ואת רשימת השינויים, ושומר על הבחירה הנוכחית אם אפשר.</summary>
        private async Task Reload(string busy = null)
        {
            var keepCategory = (CategoryTree.SelectedItem as CategoryNode)?.Id;
            var keepBook = _book?.Id;
            if (busy != null) ShowBusy(busy, "");
            try
            {
                _ro?.Dispose();
                _ro = null;
                var path = _choice.Path;
                var (db, catalog) = await Task.Run(() =>
                {
                    var d = SeforimDb.Open(path, readOnly: true);
                    return (d, d.LoadCatalog());
                });
                _ro = db;
                _catalog = catalog;
                CategoryTree.ItemsSource = _catalog.Roots;
                if (!string.IsNullOrWhiteSpace(SearchBox.Text)) ApplySearch();
                else if (keepCategory.HasValue && _catalog.Categories.TryGetValue(keepCategory.Value, out var cat)) ShowCategoryBooks(cat);
                else { BookList.ItemsSource = null; ListTitle.Text = "בחר קטגוריה"; ListSubtitle.Text = $"{_catalog.Books.Count:N0} ספרים ב-{_catalog.Categories.Count:N0} קטגוריות"; }

                if (keepBook.HasValue && _catalog.Books.TryGetValue(keepBook.Value, out var b)) ShowBook(b);
                else if (_book != null) ClearDetails();
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "לא ניתן לפתוח את הקובץ", ex.Message);
            }
            finally
            {
                if (busy != null) HideBusy();
            }
            RefreshStatus();
        }

        // ───────────────────────── מצב ─────────────────────────

        private void RefreshStatus()
        {
            if (_choice == null || _ro == null) return;
            var root = _cfg.ResolveBackupRoot();
            _storeAvailable = false;
            _ops = new List<AppliedOp>();
            if (root != null)
            {
                var storePath = BackupStore.PathFor(root, _choice.Kind, _choice.Path);
                if (File.Exists(storePath))
                {
                    try
                    {
                        using (var s = LibrarySession.OpenReadOnly(_choice.Kind, _choice.Path, root))
                        {
                            _ops = s.Ops();
                            _settings.Mirror(_cfg, s);
                            SaveSettings();
                        }
                        _storeAvailable = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
                else _storeAvailable = true;
            }

            var version = LibraryLocator.ReadVersion(_choice.Kind, _choice.Path, _ro);
            _status = _storeAvailable ? LibrarySession.StatusOf(_ops, version) : _cfg.LastStatus;
            var running = LibraryLocator.IsRunning(_choice.Kind);
            _lastRunning = running;
            var name = LibraryLocator.DisplayName(_choice.Kind);
            var count = _storeAvailable ? _ops.Count : _cfg.IntentsMirror.Count;

            switch (_status)
            {
                case LibraryStatus.Original:
                    SetPill("● קובץ מקורי", "Success", "SuccessSoft");
                    break;
                case LibraryStatus.Edited:
                    SetPill($"● ערוך: {count} שינויים", "Primary", "PrimarySoft");
                    break;
                default:
                    SetPill("● ממתין להחלה מחדש", "Warn", "WarnSoft");
                    break;
            }

            if (running && _choice.Kind != LibraryKind.Other)
                ShowBanner($"{name} פתוחה כרגע. אפשר לעיין ולחפש, אבל כדי לשמור שינויים יש לסגור אותה.", "בדוק שוב", RefreshStatus);
            else if (string.IsNullOrEmpty(_cfg.BackupRoot))
                ShowBanner("עוד לא נבחרה תיקיית גיבוי. היא נדרשת לפני השינוי הראשון (מומלץ כרטיס זיכרון או כונן חיצוני).", "בחר תיקייה…", () => ChooseBackup_Click(null, null));
            else if (root == null)
                ShowBanner($"תיקיית הגיבוי לא זמינה ({Ltr(_cfg.BackupRoot)}). אם היא בכרטיס זיכרון, חבר אותו.", "בדוק שוב", RefreshStatus);
            else if (_status == LibraryStatus.WaitingReapply)
                ShowBanner("השינויים שלך הוחזרו (למשל לקראת עדכון) ועדיין לא הוחלו שוב.", "החל עכשיו", () => Reapply_Click(null, null));
            else if (_suggestCompact)
                // אחרי מחיקה הקובץ לא קטן מעצמו; המקום מתפנה רק בדחיסה.
                ShowBanner("המחיקה הושלמה. כדי שהמקום בדיסק יתפנה בפועל, צריך לדחוס את הקובץ (לוקח כמה דקות).", "פנה מקום עכשיו", () => Compact_Click(null, null));
            else HideBanner();

            FillChangesTab(version, root);
            FillBackupState(root);
        }

        private void SetPill(string text, string fg, string bg)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource(fg);
            StatusPill.Background = (Brush)FindResource(bg);
        }

        private Action _bannerAction;

        private void ShowBanner(string text, string button = null, Action action = null)
        {
            Banner.Visibility = Visibility.Visible;
            RichText.Set(BannerText, text);
            _bannerAction = action;
            BannerButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
            BannerButton.Content = button;
        }

        private void HideBanner() => Banner.Visibility = Visibility.Collapsed;

        private void BannerButton_Click(object sender, RoutedEventArgs e) => _bannerAction?.Invoke();

        private void FillChangesTab(long? version, string root)
        {
            var name = LibraryLocator.DisplayName(_choice.Kind);
            switch (_status)
            {
                case LibraryStatus.Original:
                    StateTitle.Text = "הקובץ במצב מקורי";
                    StateBody.Text = "אין בקובץ שינויים שלך. עדכוני ספרייה יעבדו כרגיל.";
                    break;
                case LibraryStatus.Edited:
                    StateTitle.Text = "השינויים שלך מוחלים בקובץ";
                    StateBody.Text = _choice.Kind == LibraryKind.Zayit
                        ? "זית מורידה את כל הספרייה מחדש בכל עדכון, ולכן אין צורך להכין את הקובץ לפני עדכון. " +
                          "אחרי עדכון כזה, תהליך הרקע יחיל את השינויים שלך שוב (או שתלחץ כאן \"החל את השינויים שוב\")."
                        : $"כשתצא גרסת ספרייה חדשה, צריך להחזיר את הקובץ למקור לפני העדכון, כדי ש{name} תוריד רק את ההפרשים. " +
                          "תהליך הרקע עושה זאת לבד כשהתוכנה סגורה. אפשר גם ללחוץ כאן על \"החזר הכול למקור\", לעדכן, ואחר כך ללחוץ \"החל את השינויים שוב\".";
                    break;
                default:
                    StateTitle.Text = "ממתין להחלה מחדש";
                    StateBody.Text = $"השינויים הוחזרו והקובץ במצב מקורי. אם עוד לא עדכנת, זה הזמן לעדכן את הספרייה ב{name}. " +
                                     $"אחרי העדכון (ו{name} סגורה) לחץ \"החל את השינויים שוב\".";
                    break;
            }

            var hasApplied = _ops.Any(o => o.State == OpState.Applied && o.DbVersion == version);
            RestoreAllButton.IsEnabled = _storeAvailable && hasApplied;
            TopRestoreButton.IsEnabled = RestoreAllButton.IsEnabled;
            RestoreAllButton.Content = _choice.Kind == LibraryKind.Otzaria ? "↩  החזר הכול למקור (לפני עדכון)" : "↩  החזר הכול למקור";
            UndoLastButton.IsEnabled = _storeAvailable && hasApplied;
            ReapplyButton.IsEnabled = _storeAvailable && _status == LibraryStatus.WaitingReapply;
            CompactButton.IsEnabled = _choice != null;

            OpsList.ItemsSource = (_storeAvailable ? _ops.Select(ToRow) : _cfg.IntentsMirror.Select(i => new OpRow
            {
                Summary = i.Summary,
                Details = "הפרטים שמורים בתיקיית הגיבוי, שאינה זמינה כרגע",
                StateText = "",
                StateBrush = (Brush)FindResource("Muted"),
            })).ToList();
            OpsEmpty.Visibility = ((List<OpRow>)OpsList.ItemsSource).Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            OpRow ToRow(AppliedOp o)
            {
                var applied = o.State == OpState.Applied && o.DbVersion == version;
                return new OpRow
                {
                    Summary = o.Intent.Summary,
                    Details = $"{o.Created:dd/MM/yyyy HH:mm} · {o.Rows:N0} שורות בגיבוי" + (o.DbVersion.HasValue ? $" · גרסת ספרייה {o.DbVersion}" : ""),
                    StateText = applied ? "מוחל" : "ממתין להחלה",
                    StateBrush = (Brush)FindResource(applied ? "Primary" : "Warn"),
                };
            }

            try
            {
                var size = new FileInfo(_choice.Path).Length;
                var drive = new DriveInfo(Path.GetPathRoot(_choice.Path));
                var text = $"הקובץ:\n{Ltr(_choice.Path)}\nגודל: {Gb(size)} · גרסת ספרייה: {version?.ToString() ?? "לא ידועה"}\n" +
                           $"מקום פנוי בכונן {Ltr(drive.Name.TrimEnd('\\'))}: {Gb(drive.AvailableFreeSpace)}";
                if (root != null && File.Exists(BackupStore.PathFor(root, _choice.Kind, _choice.Path)))
                    text += $"\nגודל הגיבוי: {Gb(new FileInfo(BackupStore.PathFor(root, _choice.Kind, _choice.Path)).Length)}";
                RichText.Set(DiskInfo, text);
            }
            catch (Exception ex)
            {
                DiskInfo.Text = ex.Message;
            }
        }

        private void FillBackupState(string root)
        {
            if (string.IsNullOrEmpty(_cfg.BackupRoot))
            {
                BackupState.Text = "לא נבחרה תיקייה.";
                BackupState.Foreground = (Brush)FindResource("Warn");
            }
            else if (root == null)
            {
                BackupState.Text = "התיקייה לא זמינה כרגע (הכרטיס לא מחובר?).";
                BackupState.Foreground = (Brush)FindResource("Warn");
            }
            else
            {
                RichText.Set(BackupState, "✔ זמינה" + (LibraryLocator.SamePath(root, _cfg.BackupRoot) ? "" : $" (נמצאה ב-{Ltr(root)})"));
                BackupState.Foreground = (Brush)FindResource("Success");
            }
            TrayState.Text = TrayLauncher.IsRunning() ? "✔ תהליך הרקע פועל" : "תהליך הרקע לא פועל";
        }

        private static string Gb(long bytes) => Ltr(
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("N1", CultureInfo.InvariantCulture) + " GB"
            : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("N0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KB");

        /// <summary>עוטף נתיב או מידה בכיווניות משמאל לימין, כדי שלא יתפרקו בתוך משפט בעברית.</summary>
        private static string Ltr(string s) => RichText.Ltr(s);

        // ───────────────────────── עיון ─────────────────────────

        private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(CategoryTree.SelectedItem is CategoryNode cat)) return;
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            ShowCategoryBooks(cat);
            ShowCategory(cat);
        }

        private void ShowCategoryBooks(CategoryNode cat)
        {
            var books = cat.AllBooks().ToList();
            BookList.ItemsSource = books;
            ListTitle.Text = cat.Title;
            ListSubtitle.Text = $"{cat.Path.Replace("/", " › ")} · {books.Count:N0} ספרים";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void ApplySearch()
        {
            if (_catalog == null) return;
            var q = SearchBox.Text.Trim();
            if (q.Length == 0)
            {
                if (CategoryTree.SelectedItem is CategoryNode cat) ShowCategoryBooks(cat);
                else BookList.ItemsSource = null;
                return;
            }
            var results = _catalog.Books.Values
                .Where(b => b.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (b.Authors ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(b => b.Title.StartsWith(q) ? 0 : 1).ThenBy(b => b.Title.Length)
                .Take(1000).ToList();
            BookList.ItemsSource = results;
            ListTitle.Text = $"תוצאות חיפוש: \"{q}\"";
            ListSubtitle.Text = results.Count >= 1000 ? "מוצגות 1,000 התוצאות הראשונות" : $"{results.Count:N0} ספרים";
        }

        private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var n = BookList.SelectedItems.Count;
            DeleteBooksButton.IsEnabled = n > 0;
            SelectionInfo.Text = n > 1 ? $"נבחרו {n:N0} ספרים" : "";
            if (n == 1 && BookList.SelectedItem is BookInfo b) ShowBook(b);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e) => BookList.SelectAll();

        private void ClearDetails()
        {
            _book = null;
            _category = null;
            DetailsEmpty.Visibility = Visibility.Visible;
            BookPanel.Visibility = Visibility.Collapsed;
            CategoryPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowBook(BookInfo b)
        {
            _book = b;
            _category = null;
            _pendingMove = null;
            try
            {
                _details = _ro.LoadDetails(b);
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "שגיאה בקריאת הספר", ex.Message);
                return;
            }
            _filling = true;
            BookTitle.Text = b.Title;
            BookAuthors.Text = string.Join(", ", _details.Authors);
            BookShortDesc.Text = _details.ShortDesc ?? "";
            LongDescPanel.Visibility = _details.HasLongDesc ? Visibility.Visible : Visibility.Collapsed;
            BookLongDesc.Text = _details.LongDesc ?? "";
            BookCategory.Text = b.Category?.Path.Replace("/", " › ") ?? "";
            BookOrder.Text = b.OrderIndex.ToString(CultureInfo.InvariantCulture);
            BookMeta.Text = $"{b.TotalLines:N0} שורות" + (string.IsNullOrEmpty(_details.Source) ? "" : $" · מקור: {_details.Source}") +
                            (_details.BaseBooks.Count > 0 ? $" · על: {string.Join(", ", _details.BaseBooks.Take(3))}" : "") + $" · מזהה {b.Id}";
            _filling = false;
            UpdateBookDirty();
            PreviewText.Text = "";
            if (PreviewExpander.IsExpanded) LoadPreview();
            DetailsEmpty.Visibility = Visibility.Collapsed;
            CategoryPanel.Visibility = Visibility.Collapsed;
            BookPanel.Visibility = Visibility.Visible;
        }

        private void Preview_Expanded(object sender, RoutedEventArgs e) => LoadPreview();

        private void LoadPreview()
        {
            if (_book == null || _ro == null) return;
            try
            {
                var lines = _ro.ReadLines(_book.Id, 0, 60);
                PreviewText.Text = string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
            }
            catch (Exception ex)
            {
                PreviewText.Text = "לא ניתן להציג: " + ex.Message;
            }
        }

        private void ShowCategory(CategoryNode c)
        {
            _category = c;
            _book = null;
            _filling = true;
            CategoryTitle.Text = c.Title;
            CategoryOrder.Text = c.OrderIndex.ToString(CultureInfo.InvariantCulture);
            CategoryMeta.Text = $"{c.Path.Replace("/", " › ")}\n{c.TotalBooks:N0} ספרים, {c.SelfAndDescendants().Count() - 1:N0} תתי-קטגוריות · מזהה {c.Id}";
            _filling = false;
            SaveCategoryButton.IsEnabled = false;
            DeleteCategoryButton.Content = $"מחק קטגוריה ({c.TotalBooks:N0} ספרים)";
            DetailsEmpty.Visibility = Visibility.Collapsed;
            BookPanel.Visibility = Visibility.Collapsed;
            CategoryPanel.Visibility = Visibility.Visible;
        }

        // ───────────────────────── עריכת ספר ─────────────────────────

        private void BookField_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_filling) UpdateBookDirty();
        }

        private BookChanges CollectBookChanges()
        {
            if (_book == null || _details == null) return null;
            var ch = new BookChanges();
            var title = BookTitle.Text.Trim();
            if (title.Length > 0 && title != _book.Title) ch.Title = title;
            var authors = SplitAuthors(BookAuthors.Text);
            if (!authors.SequenceEqual(_details.Authors)) ch.Authors = authors;
            if (BookShortDesc.Text.Trim() != (_details.ShortDesc ?? "").Trim()) ch.ShortDesc = BookShortDesc.Text.Trim();
            if (_details.HasLongDesc && BookLongDesc.Text.Trim() != (_details.LongDesc ?? "").Trim()) ch.LongDesc = BookLongDesc.Text.Trim();
            if (int.TryParse(BookOrder.Text.Trim(), out var order) && order != _book.OrderIndex) ch.OrderIndex = order;
            if (_pendingMove != null && _pendingMove != _book.Category) ch.MoveTo = CategoryRef.Of(_pendingMove);
            return ch;
        }

        private static List<string> SplitAuthors(string text) =>
            text.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList();

        private void UpdateBookDirty()
        {
            var ch = CollectBookChanges();
            var dirty = ch != null && !ch.IsEmpty;
            SaveBookButton.IsEnabled = dirty;
            RevertBookButton.IsEnabled = dirty;
        }

        private void RevertBook_Click(object sender, RoutedEventArgs e)
        {
            if (_book != null) ShowBook(_book);
        }

        private void MoveBook_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null) return;
            var picker = new CategoryPicker(this, _catalog.Roots, _book.Category);
            if (picker.ShowDialog() == true && picker.Selected != null)
            {
                _pendingMove = picker.Selected;
                BookCategory.Text = _pendingMove.Path.Replace("/", " › ");
                UpdateBookDirty();
            }
        }

        private async void SaveBook_Click(object sender, RoutedEventArgs e)
        {
            var ch = CollectBookChanges();
            if (ch == null || ch.IsEmpty || _book == null) return;
            var book = _book;
            await RunWrite("שומר את השינויים…", (s, p) =>
            {
                s.EditBook(Intents.EditBook(book, ch), book.Id, ch, p);
                return null;
            });
        }

        private void DeleteOneBook_Click(object sender, RoutedEventArgs e)
        {
            if (_book != null) _ = DeleteBooks(new List<BookInfo> { _book });
        }

        private void DeleteBooks_Click(object sender, RoutedEventArgs e)
        {
            var books = BookList.SelectedItems.Cast<BookInfo>().ToList();
            if (books.Count > 0) _ = DeleteBooks(books);
        }

        private async Task DeleteBooks(List<BookInfo> books)
        {
            var intent = Intents.DeleteBooks(books);
            await RunDelete(intent, books.Select(b => b.Id).ToList(), null,
                books.Count == 1 ? $"למחוק את \"{books[0].Title}\"?" : $"למחוק {books.Count:N0} ספרים?");
        }

        // ───────────────────────── עריכת קטגוריה ─────────────────────────

        private void CategoryField_Changed(object sender, TextChangedEventArgs e)
        {
            if (_filling || _category == null) return;
            SaveCategoryButton.IsEnabled = CollectCategoryChanges() is CategoryChanges ch && !ch.IsEmpty;
        }

        private CategoryChanges CollectCategoryChanges()
        {
            if (_category == null) return null;
            var ch = new CategoryChanges();
            var title = CategoryTitle.Text.Trim();
            if (title.Length > 0 && title != _category.Title) ch.Title = title;
            if (int.TryParse(CategoryOrder.Text.Trim(), out var order) && order != _category.OrderIndex) ch.OrderIndex = order;
            return ch;
        }

        private async void SaveCategory_Click(object sender, RoutedEventArgs e)
        {
            var ch = CollectCategoryChanges();
            if (ch == null || ch.IsEmpty) return;
            var cat = _category;
            await RunWrite("שומר את השינויים…", (s, p) =>
            {
                s.EditCategory(Intents.EditCategory(cat, ch), cat.Id, ch, p);
                return null;
            });
        }

        private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (_category == null) return;
            var cat = _category;
            await RunDelete(Intents.DeleteCategories(new[] { cat }), null, new List<long> { cat.Id },
                $"למחוק את הקטגוריה \"{cat.Title}\" וכל מה שבתוכה ({cat.TotalBooks:N0} ספרים)?");
        }

        // ───────────────────────── מחיקה ─────────────────────────

        private async Task RunDelete(OpIntent intent, List<long> bookIds, List<long> categoryIds, string question)
        {
            if (!EnsureCanWrite()) return;
            LibrarySession session = null;
            var progress = new Progress<string>(t => BusyText.Text = t);
            ShowBusy("מחשב מה יימחק…", "");
            try
            {
                var choice = _choice;
                var root = _cfg.ResolveBackupRoot();
                _ro?.Dispose();
                _ro = null;
                session = await Task.Run(() => LibrarySession.OpenForWrite(choice.Kind, choice.Path, root));
                var s = session;
                var plan = await Task.Run(() => s.Engine.PlanDelete(intent, bookIds, categoryIds, progress));
                HideBusy();
                if (plan.Blockers.Count > 0)
                {
                    Dialogs.Error(this, "לא ניתן למחוק", string.Join("\n", plan.Blockers));
                    return;
                }
                if (!Dialogs.Ask(this, question, DescribePlan(plan, root), "מחק", "ביטול", danger: true)) return;
                ShowBusy("מוחק…", "");
                await Task.Run(() => s.Delete(plan, progress));
                _suggestCompact = true;
                _settings.Mirror(_cfg, s);
                SaveSettings();
            }
            catch (Exception ex)
            {
                HideBusy();
                Dialogs.Error(this, "המחיקה לא בוצעה", ex.Message);
            }
            finally
            {
                session?.Dispose();
                HideBusy();
            }
            ClearDetails();
            await Reload("מרענן…");
            OfferBackgroundProcess();
        }

        private string DescribePlan(DeletePlan plan, string root)
        {
            var lines = new List<string>();
            var books = plan.Count("book");
            var cats = plan.Count("category");
            if (cats > 0) lines.Add($"• {cats:N0} קטגוריות");
            lines.Add($"• {books:N0} ספרים");
            if (plan.Count("line") > 0) lines.Add($"• {plan.Count("line"):N0} שורות טקסט");
            if (plan.Count("link") > 0) lines.Add($"• {plan.Count("link"):N0} קישורים למפרשים ולמקורות");
            var other = plan.TotalRows - books - cats - plan.Count("line") - plan.Count("link");
            if (other > 0) lines.Add($"• {other:N0} רשומות נלוות (תוכן עניינים, אינדקסים ועוד)");
            var text = "יימחקו מהקובץ:\n" + string.Join("\n", lines) +
                       $"\n\nהכול נשמר קודם בתיקיית הגיבוי ({Ltr(root)}), ואפשר להחזיר בכל עת.";
            if (_choice.Kind == LibraryKind.Zayit)
                text += "\n\nשים לב: בזית הספרים יוסרו גם מעץ הספרים, אבל ייתכן שימשיכו להופיע בתוצאות החיפוש, כי לזית יש אינדקס חיפוש נפרד.";
            if (_choice.Kind == LibraryKind.Otzaria)
                text += "\n\nייתכן שהספרים ימשיכו להופיע בתוצאות החיפוש של אוצריא עד לבניית אינדקס החיפוש מחדש.";
            return text;
        }

        // ───────────────────────── שינויים ועדכונים ─────────────────────────

        private async void RestoreAll_Click(object sender, RoutedEventArgs e)
        {
            var name = LibraryLocator.DisplayName(_choice.Kind);
            if (!Dialogs.Ask(this, "להחזיר את הקובץ למקור?",
                    $"כל השינויים יוחזרו, והקובץ יהיה זהה בדיוק לגרסה המקורית. השינויים לא נמחקים: אחרי שתעדכן את הספרייה ב{name}, לחץ \"החל את השינויים שוב\".",
                    "החזר למקור")) return;
            var ok = await RunWrite("מחזיר את הקובץ למקור…", (s, p) => s.RestoreAll(p));
            if (ok) Dialogs.Info(this, "הקובץ חזר למקור", $"עכשיו אפשר לפתוח את {name} ולעדכן את הספרייה. בסיום, סגור את {name} וחזור לכאן כדי להחיל את השינויים שוב.");
        }

        private async void Reapply_Click(object sender, RoutedEventArgs e)
        {
            ReapplyResult result = null;
            var ok = await RunWrite("מחיל את השינויים שוב…", (s, p) => result = s.ReapplyRestored(p));
            if (!ok || result == null) return;
            var text = $"הוחלו {result.Applied} שינויים.";
            if (result.Notes.Count > 0) text += "\n\n" + string.Join("\n", result.Notes);
            if (result.Problems.Count > 0)
                Dialogs.Error(this, "חלק מהשינויים לא הוחלו", text + "\n\nבעיות:\n" + string.Join("\n", result.Problems));
            else Dialogs.Info(this, "השינויים הוחלו", text);
        }

        private async void UndoLast_Click(object sender, RoutedEventArgs e)
        {
            var last = _ops.Where(o => o.State == OpState.Applied).OrderByDescending(o => o.Id).FirstOrDefault();
            if (last == null) return;
            if (!Dialogs.Ask(this, "לבטל את השינוי האחרון?", last.Intent.Summary, "בטל את השינוי")) return;
            await RunWrite("מבטל…", (s, p) => s.UndoLast(p));
        }

        private async void Compact_Click(object sender, RoutedEventArgs e)
        {
            var size = new FileInfo(_choice.Path).Length;
            if (!Dialogs.Ask(this, "לפנות מקום בדיסק?",
                    $"אחרי מחיקה הקובץ לא קטן מעצמו. הדחיסה בונה אותו מחדש בלי החלקים שנמחקו. זה עשוי לקחת כמה דקות, " +
                    $"ובזמן הזה נדרש מקום פנוי זמני של עד {Gb(size)}.", "דחוס עכשיו")) return;
            var ok = await RunWrite("דוחס את הקובץ…", (s, p) => { s.Compact(p); return null; }, needsBackup: false);
            if (!ok) return;
            _suggestCompact = false;
            RefreshStatus();
            Dialogs.Info(this, "הדחיסה הסתיימה", $"גודל הקובץ: {Gb(new FileInfo(_choice.Path).Length)} (לפני: {Gb(size)}).");
        }

        // ───────────────────────── העתקה ללוח ─────────────────────────

        /// <summary>
        /// שם את קובץ הספרייה (כפי שהוא עכשיו, עם השינויים) בלוח כקובץ, כך שאפשר להדביק אותו
        /// בתיקייה אחרת. בזית מצורף גם catalog.pb, שבלעדיו זית לא תציג את השינויים בעץ.
        /// </summary>
        private void CopyFile_Click(object sender, RoutedEventArgs e)
        {
            if (_choice == null) return;
            try
            {
                // בזית (מצב WAL) חלק מהשינויים עשוי לשבת בקובץ ‎-wal; מכניסים אותם לקובץ הראשי.
                var mode = _ro?.Scalar("PRAGMA journal_mode") as string;
                if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    if (LibraryLocator.IsRunning(_choice.Kind))
                    {
                        Dialogs.Info(this, "יש לסגור את " + LibraryLocator.DisplayName(_choice.Kind),
                            "כדי להעתיק קובץ שלם ותקין, יש לסגור קודם את " + LibraryLocator.DisplayName(_choice.Kind) + ".");
                        return;
                    }
                    using (var db = SeforimDb.Open(_choice.Path, readOnly: false)) db.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
                }
                var files = new System.Collections.Specialized.StringCollection { _choice.Path };
                var catalog = Path.Combine(Path.GetDirectoryName(_choice.Path) ?? "", ZayitCatalog.FileName);
                if (_choice.Kind == LibraryKind.Zayit && File.Exists(catalog)) files.Add(catalog);
                Clipboard.SetFileDropList(files);
                var size = Gb(new FileInfo(_choice.Path).Length);
                Dialogs.Info(this, "הקובץ הועתק ללוח",
                    $"אפשר להדביק אותו (Ctrl+V) בכל תיקייה או בכונן נייד. הגודל: {size}, ולכן ההדבקה עשויה לקחת זמן." +
                    (files.Count > 1 ? "\n\nצורף גם הקובץ catalog.pb, שזית צריכה כדי להציג את השינויים בעץ הספרים." : "") +
                    "\n\nההדבקה מעתיקה את הקובץ כפי שהוא ברגע ההדבקה, ולכן עדיף לא לבצע שינויים עד שהיא מסתיימת.");
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "ההעתקה ללוח נכשלה", ex.Message);
            }
        }

        // ───────────────────────── הרצת פעולת כתיבה ─────────────────────────

        private bool EnsureCanWrite(bool needsBackup = true)
        {
            if (_choice == null) return false;
            if (_choice.Kind != LibraryKind.Other && LibraryLocator.IsRunning(_choice.Kind))
            {
                Dialogs.Info(this, LibraryLocator.DisplayName(_choice.Kind) + " פתוחה",
                    $"כדי לשנות את הקובץ יש לסגור קודם את {LibraryLocator.DisplayName(_choice.Kind)}.");
                return false;
            }
            if (!needsBackup) return true;
            if (string.IsNullOrEmpty(_cfg.BackupRoot))
            {
                Dialogs.Info(this, "צריך לבחור תיקיית גיבוי",
                    "לפני השינוי הראשון צריך לבחור איפה לשמור את הגיבוי. מומלץ כרטיס זיכרון או כונן חיצוני, כדי שהמקום שמתפנה במחשב לא יתמלא שוב.");
                ChooseBackup_Click(null, null);
                if (string.IsNullOrEmpty(_cfg.BackupRoot)) return false;
            }
            if (_cfg.ResolveBackupRoot() == null)
            {
                Dialogs.Info(this, "תיקיית הגיבוי לא זמינה", $"לא נמצאה התיקייה {Ltr(_cfg.BackupRoot)}. אם היא בכרטיס זיכרון, חבר אותו ונסה שוב.");
                return false;
            }
            return true;
        }

        private async Task<bool> RunWrite(string busy, Func<LibrarySession, IProgress<string>, object> action, bool needsBackup = true)
        {
            if (!EnsureCanWrite(needsBackup)) return false;
            var progress = new Progress<string>(t => BusyText.Text = t);
            ShowBusy(busy, "");
            var ok = false;
            var choice = _choice;
            var root = _cfg.ResolveBackupRoot();
            try
            {
                _ro?.Dispose();
                _ro = null;
                await Task.Run(() =>
                {
                    if (root == null && !needsBackup)
                    {
                        // דחיסה אינה דורשת גיבוי: פותחים רק את הקובץ.
                        using (var db = SeforimDb.Open(choice.Path, readOnly: false))
                        {
                            ((IProgress<string>)progress).Report("דוחס את הקובץ…");
                            db.Exec("VACUUM main");
                        }
                        return;
                    }
                    using (var s = LibrarySession.OpenForWrite(choice.Kind, choice.Path, root))
                    {
                        action(s, progress);
                        _settings.Mirror(_cfg, s);
                    }
                });
                SaveSettings();
                ok = true;
            }
            catch (Exception ex)
            {
                HideBusy();
                Dialogs.Error(this, "הפעולה לא בוצעה", ex.Message);
            }
            finally
            {
                HideBusy();
            }
            await Reload();
            if (ok) OfferBackgroundProcess();
            return ok;
        }

        /// <summary>אחרי השינוי הראשון שואלים פעם אחת אם להפעיל את תהליך הרקע.</summary>
        private void OfferBackgroundProcess()
        {
            if (_settings.AskedAboutTray || _choice == null || _choice.Kind == LibraryKind.Other || _ops.Count == 0) return;
            _settings.AskedAboutTray = true;
            SaveSettings();
            if (Autostart.IsEnabled()) return;
            var name = LibraryLocator.DisplayName(_choice.Kind);
            if (!Dialogs.Ask(this, "להפעיל את תהליך הרקע?",
                    $"כשתצא גרסת ספרייה חדשה, תהליך הרקע יציע להחזיר את הקובץ למקור כדי ש{name} תוריד רק את ההפרשים, ואחרי העדכון יחיל את השינויים שלך שוב. " +
                    "הוא יושב ליד השעון, כמעט לא צורך משאבים, ותמיד אפשר לדחות או לכבות אותו בלשונית ההגדרות.",
                    "הפעל", "לא עכשיו")) return;
            try
            {
                Autostart.Set(true);
                _settings.StartWithWindows = true;
                SaveSettings();
                if (!TrayLauncher.IsRunning()) TrayLauncher.Start();
                StartWithWindows.IsChecked = true;
                FillBackupState(_cfg?.ResolveBackupRoot());
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "לא ניתן להפעיל את תהליך הרקע", ex.Message);
            }
        }

        private void ShowBusy(string title, string text)
        {
            BusyTitle.Text = title;
            BusyText.Text = text;
            BusyOverlay.Visibility = Visibility.Visible;
        }

        private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

        // ───────────────────────── פתיחת קובץ אחר ─────────────────────────

        private void OpenOther_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "בחירת קובץ ספרייה",
                Filter = "ספריית ספרים (*.db)|*.db|כל הקבצים|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            var path = dlg.FileName;
            try
            {
                using (SeforimDb.Open(path, readOnly: true)) { }
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "לא ניתן לפתוח את הקובץ", ex.Message);
                return;
            }
            var kind = LibraryLocator.GuessKind(path);
            var list = ((List<LibraryChoice>)LibraryCombo.ItemsSource) ?? new List<LibraryChoice>();
            var existing = list.FirstOrDefault(c => LibraryLocator.SamePath(c.Path, path));
            if (existing == null)
            {
                existing = new LibraryChoice { Kind = kind, Path = path, Display = kind == LibraryKind.Other ? Path.GetFileName(Path.GetDirectoryName(path)) + " (" + Path.GetFileName(path) + ")" : LibraryLocator.DisplayName(kind) };
                list.Add(existing);
                _settings.Get(kind, path);
                SaveSettings();
                LibraryCombo.ItemsSource = null;
                LibraryCombo.ItemsSource = list;
            }
            LibraryCombo.SelectedItem = existing;
        }

        // ───────────────────────── הגדרות ─────────────────────────

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == Tabs && _choice != null && _ro != null) RefreshStatus();
        }

        private void ChooseBackup_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
                   {
                       Description = "בחר תיקייה לגיבוי (מומלץ כרטיס זיכרון או כונן חיצוני)",
                       ShowNewFolderButton = true,
                       SelectedPath = _cfg?.BackupRoot ?? "",
                   })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var chosen = dlg.SelectedPath;
                try
                {
                    var test = Path.Combine(chosen, BackupStore.FolderName);
                    Directory.CreateDirectory(test);
                    var probe = Path.Combine(test, ".write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    Dialogs.Error(this, "אי אפשר לכתוב לתיקייה הזו", ex.Message);
                    return;
                }
                if (_choice != null && string.Equals(Path.GetPathRoot(chosen), Path.GetPathRoot(_choice.Path), StringComparison.OrdinalIgnoreCase) &&
                    !Dialogs.Ask(this, "התיקייה באותו כונן של הספרייה",
                        "גיבוי באותו כונן לא יחסוך מקום: כל מה שיימחק מהספרייה יישמר באותו כונן. להמשיך בכל זאת?", "המשך"))
                    return;

                if (_cfg != null && _choice != null)
                {
                    var oldRoot = _cfg.ResolveBackupRoot();
                    if (oldRoot != null && !LibraryLocator.SamePath(oldRoot, chosen))
                    {
                        var from = BackupStore.PathFor(oldRoot, _choice.Kind, _choice.Path);
                        var to = BackupStore.PathFor(chosen, _choice.Kind, _choice.Path);
                        if (File.Exists(from) && !File.Exists(to))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(to));
                            File.Copy(from, to);
                            File.Delete(from);
                        }
                    }
                    _cfg.BackupRoot = chosen;
                    foreach (var other in _settings.Libraries.Where(l => string.IsNullOrEmpty(l.BackupRoot))) other.BackupRoot = chosen;
                    SaveSettings();
                    BackupPath.Text = chosen;
                    RefreshStatus();
                }
            }
        }

        private void StartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Autostart.Set(StartWithWindows.IsChecked == true);
                _settings.StartWithWindows = StartWithWindows.IsChecked == true;
                SaveSettings();
                if (_settings.StartWithWindows && !TrayLauncher.IsRunning()) TrayLauncher.Start();
                FillBackupState(_cfg?.ResolveBackupRoot());
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "לא ניתן לשנות את ההגדרה", ex.Message);
                StartWithWindows.IsChecked = Autostart.IsEnabled();
            }
        }

        private void WatchThisLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null) return;
            _cfg.Watch = WatchThisLibrary.IsChecked == true;
            SaveSettings();
        }

        private void CheckHours_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CheckHours.SelectedItem is int h && h != _settings.CheckHours)
            {
                _settings.CheckHours = h;
                SaveSettings();
            }
        }

        private void StartTray_Click(object sender, RoutedEventArgs e)
        {
            if (!TrayLauncher.IsRunning()) TrayLauncher.Start();
            TrayState.Text = "✔ תהליך הרקע פועל";
        }

        private void SaveSettings()
        {
            try
            {
                // תהליך הרקע עשוי לעדכן שדות משלו; ממזגים כדי לא לדרוס אותם.
                var onDisk = AppSettings.Load();
                _settings.LastReleaseCheck = onDisk.LastReleaseCheck ?? _settings.LastReleaseCheck;
                foreach (var l in _settings.Libraries)
                {
                    var d = onDisk.Libraries.FirstOrDefault(x => x.Kind == l.Kind && (l.Kind != LibraryKind.Other || LibraryLocator.SamePath(x.DbPath, l.DbPath)));
                    if (d == null) continue;
                    l.NotifiedReleaseVersion = d.NotifiedReleaseVersion ?? l.NotifiedReleaseVersion;
                    l.SnoozeUntil = d.SnoozeUntil ?? l.SnoozeUntil;
                }
                _settings.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
