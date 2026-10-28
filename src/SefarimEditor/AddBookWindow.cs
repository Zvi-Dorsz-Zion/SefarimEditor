using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sefarim.Core;

namespace SefarimEditor
{
    /// <summary>חלון "הוספת ספר" (זית בלבד): בחירת קובץ טקסט, שם, מחברים, תיאור וקטגוריה, עם תצוגה מקדימה.</summary>
    internal sealed class AddBookWindow : Window
    {
        private readonly Catalog _catalog;
        private readonly TextBox _file = new TextBox { IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight };
        private readonly TextBox _title = new TextBox();
        private readonly TextBox _authors = new TextBox();
        private readonly TextBox _desc = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 46 };
        private readonly TextBlock _category = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _newCategory = new CheckBox { Content = "ליצור קטגוריה חדשה בתוך הקטגוריה שנבחרה", Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBox _newCategoryName = new TextBox { IsEnabled = false, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private readonly TextBox _preview = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 150, FontSize = 13,
        };
        private readonly Button _ok = new Button { Content = "הוסף את הספר", IsDefault = true, IsEnabled = false, MinWidth = 110 };
        private CategoryNode _selected;
        private BookText _text;

        public AddBookSpec Spec { get; private set; }
        public byte[] FileBytes { get; private set; }

        public AddBookWindow(Window owner, Catalog catalog, CategoryNode initial)
        {
            _catalog = catalog;
            _selected = initial;
            Title = "הוספת ספר לזית";
            Owner = owner;
            Width = 640;
            // במסך נמוך הטופס נגלל, והכפתורים נשארים תמיד למטה.
            Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
            MinHeight = 420;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Style = (Style)Application.Current.FindResource("AppWindow");
            Background = (Brush)Application.Current.FindResource("Surface");
            _ok.Style = (Style)Application.Current.FindResource("PrimaryButton");

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "הוספת ספר לזית", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) });
            var intro = Muted("");
            RichText.Set(intro, $"קובץ טקסט, שורה לכל שורה. כותרות מסומנות בתגיות {RichText.Ltr("<h1>")} עד {RichText.Ltr("<h6>")} " +
                                "(כמו בספרים של אוצריא וזית), והן הופכות לתוכן העניינים של הספר. קובץ בלי כותרות יקבל כותרת אחת בשם הספר.");
            panel.Children.Add(intro);

            var fileRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var browse = new Button { Content = "בחר קובץ…", Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) => ChooseFile();
            DockPanel.SetDock(browse, Dock.Right);
            fileRow.Children.Add(browse);
            fileRow.Children.Add(_file);
            panel.Children.Add(Label("קובץ"));
            panel.Children.Add(fileRow);

            panel.Children.Add(Label("שם הספר"));
            panel.Children.Add(_title);
            panel.Children.Add(Label("מחברים (מופרדים בפסיק)"));
            panel.Children.Add(_authors);
            panel.Children.Add(Label("תיאור קצר (לא חובה)"));
            panel.Children.Add(_desc);

            panel.Children.Add(Label("קטגוריה"));
            var catRow = new DockPanel();
            var pick = new Button { Content = "בחר…", Margin = new Thickness(8, 0, 0, 0) };
            pick.Click += (s, e) => ChooseCategory();
            DockPanel.SetDock(pick, Dock.Right);
            catRow.Children.Add(pick);
            catRow.Children.Add(_category);
            panel.Children.Add(catRow);
            panel.Children.Add(_newCategory);
            panel.Children.Add(_newCategoryName);
            _newCategory.Checked += (s, e) => { _newCategoryName.IsEnabled = true; _newCategoryName.Focus(); Validate(); };
            _newCategory.Unchecked += (s, e) => { _newCategoryName.IsEnabled = false; Validate(); };

            panel.Children.Add(Label("תצוגה מקדימה"));
            panel.Children.Add(_info);
            panel.Children.Add(_preview);

            var note = new Border
            {
                Background = (Brush)Application.Current.FindResource("WarnSoft"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.FindResource("Warn"),
                    Text = "הספר יופיע בזית בעץ הספרים ואפשר יהיה לעיין בו, אבל הוא לא יופיע בתוצאות החיפוש של זית " +
                           "(לזית יש אינדקס חיפוש נפרד). עותק של הקובץ נשמר בתיקיית הגיבוי, כדי שאחרי עדכון של זית הספר יתווסף שוב.",
                },
            };
            panel.Children.Add(note);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _ok.Click += (s, e) => Accept();
            buttons.Children.Add(_ok);
            buttons.Children.Add(new Button { Content = "ביטול", IsCancel = true, MinWidth = 90 });
            var bottom = new Border
            {
                Padding = new Thickness(22, 12, 22, 14),
                BorderBrush = (Brush)Application.Current.FindResource("Line"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = buttons,
            };
            var dock = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);
            dock.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Padding = new Thickness(22, 18, 22, 12), Child = panel },
            });
            Content = dock;

            _title.TextChanged += (s, e) => { RefreshPreview(); Validate(); };
            _newCategoryName.TextChanged += (s, e) => Validate();
            ShowCategory();
            RefreshPreview();
        }

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) };

        private static TextBlock Muted(string text) =>
            new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap };

        private void ChooseFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "בחירת קובץ הספר",
                Filter = "קובצי טקסט (*.txt)|*.txt|כל הקבצים (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            LoadFile(dlg.FileName);
        }

        /// <summary>טוען קובץ (גם ממצב הבדיקה של צילום המסך).</summary>
        internal void LoadFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > BookImport.MaxFileBytes) throw new InvalidOperationException("הקובץ גדול מדי (מעל 200MB).");
                FileBytes = File.ReadAllBytes(path);
                _file.Text = path;
                if (_title.Text.Trim().Length == 0) _title.Text = BookImport.TitleFromFileName(path);
                RefreshPreview();
            }
            catch (Exception ex)
            {
                FileBytes = null;
                _text = null;
                _file.Text = "";
                Dialogs.Error(this, "לא ניתן לקרוא את הקובץ", ex.Message);
                RefreshPreview();
            }
            Validate();
        }

        private void RefreshPreview()
        {
            if (FileBytes == null)
            {
                _info.Text = "עוד לא נבחר קובץ.";
                _info.Foreground = (Brush)Application.Current.FindResource("Muted");
                _preview.Text = "";
                return;
            }
            try
            {
                var title = _title.Text.Trim();
                _text = BookImport.Read(FileBytes, title.Length > 0 ? title : "ספר");
                var parts = new List<string>
                {
                    $"{_text.Lines.Count:N0} שורות",
                    $"{_text.Headings:N0} כותרות",
                    "קידוד " + _text.EncodingName,
                };
                if (_text.HasNekudot) parts.Add("עם ניקוד");
                if (_text.HasTeamim) parts.Add("עם טעמים");
                var text = string.Join(" · ", parts);
                if (_text.AddedTitleHeading) text += "\nבקובץ אין כותרות, ולכן תיווסף בראשו כותרת בשם הספר.";
                var same = title.Length > 0 ? _catalog.Books.Values.Where(b => b.Title == title).ToList() : new List<BookInfo>();
                if (same.Count > 0) text += $"\nשים לב: כבר יש בספרייה ספר בשם הזה ({same[0].Category?.Path.Replace("/", " › ")}).";
                _info.Text = text;
                _info.Foreground = (Brush)Application.Current.FindResource(same.Count > 0 ? "Warn" : "Text");
                _preview.Text = string.Join(Environment.NewLine, _text.Lines.Take(40).Select(l =>
                {
                    var level = BookImport.HeadingLevel(l);
                    var plain = SeforimDb.StripHtml(l);
                    return level > 0 && plain.Length > 0 ? "■ " + plain : plain;
                }));
            }
            catch (Exception ex)
            {
                _text = null;
                _info.Text = "לא ניתן לקרוא את הקובץ: " + ex.Message;
                _info.Foreground = (Brush)Application.Current.FindResource("Danger");
                _preview.Text = "";
            }
        }

        private void ChooseCategory()
        {
            var picker = new CategoryPicker(this, _catalog.Roots, null, "לאיזו קטגוריה להוסיף את הספר?");
            if (picker.ShowDialog() == true && picker.Selected != null)
            {
                _selected = picker.Selected;
                ShowCategory();
                Validate();
            }
        }

        private void ShowCategory()
        {
            _category.Text = _selected == null ? "לא נבחרה קטגוריה" : _selected.Path.Replace("/", " › ");
            _category.Foreground = (Brush)Application.Current.FindResource(_selected == null ? "Muted" : "Text");
            _newCategory.Content = _selected == null
                ? "ליצור קטגוריה חדשה ברמה העליונה"
                : "ליצור קטגוריה חדשה בתוך הקטגוריה שנבחרה";
        }

        private void Validate()
        {
            var newCat = _newCategory.IsChecked == true;
            _ok.IsEnabled = _text != null && FileBytes != null && _title.Text.Trim().Length > 0 &&
                            (newCat ? _newCategoryName.Text.Trim().Length > 0 : _selected != null);
        }

        private void Accept()
        {
            Validate();
            if (!_ok.IsEnabled) return;
            var newCat = _newCategory.IsChecked == true;
            if (newCat)
            {
                var name = _newCategoryName.Text.Trim();
                var siblings = _selected == null ? _catalog.Roots : _selected.Children;
                if (siblings.Any(c => c.Title == name) &&
                    !Dialogs.Ask(this, "כבר יש קטגוריה בשם הזה", $"בתוך הקטגוריה שנבחרה כבר יש קטגוריה בשם \"{name}\". ליצור בכל זאת עוד אחת?", "צור בכל זאת"))
                    return;
            }
            Spec = new AddBookSpec
            {
                Title = _title.Text.Trim(),
                Authors = _authors.Text.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList(),
                ShortDesc = _desc.Text.Trim().Length == 0 ? null : _desc.Text.Trim(),
                Category = _selected == null ? null : CategoryRef.Of(_selected),
                NewCategoryTitle = newCat ? _newCategoryName.Text.Trim() : null,
                OriginalFileName = Path.GetFileName(_file.Text),
            };
            DialogResult = true;
        }
    }
}
