using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SefarimEditor
{
    /// <summary>חלונות הודעה ושאלה בעברית, מימין לשמאל, בעיצוב של התוכנה.</summary>
    internal static class Dialogs
    {
        public static void Info(Window owner, string title, string body) => Show(owner, title, body, "אישור", null, DialogKind.Info);

        public static void Error(Window owner, string title, string body) => Show(owner, title, body, "סגור", null, DialogKind.Error);

        public static bool Ask(Window owner, string title, string body, string ok, string cancel = "ביטול", bool danger = false) =>
            Show(owner, title, body, ok, cancel, danger ? DialogKind.Danger : DialogKind.Question);

        private enum DialogKind
        {
            Info,
            Question,
            Danger,
            Error,
        }

        private static bool Show(Window owner, string title, string body, string ok, string cancel, DialogKind kind)
        {
            var w = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.Height,
                Width = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = owner == null || !owner.IsVisible,
                Style = (Style)Application.Current.FindResource("AppWindow"),
                Background = (Brush)Application.Current.FindResource("Surface"),
            };
            if (owner != null && owner.IsVisible) w.Owner = owner;

            var icon = kind == DialogKind.Error ? "⛔" : kind == DialogKind.Danger ? "⚠" : kind == DialogKind.Question ? "❓" : "ℹ";
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock { Text = icon, FontSize = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("H1"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            panel.Children.Add(head);
            var bodyBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22, FontSize = 14.5 };
            RichText.Set(bodyBlock, body);
            panel.Children.Add(bodyBlock);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
            var okStyle = kind == DialogKind.Danger ? "DangerSolidButton" : "PrimaryButton";
            var okButton = new Button { Content = ok, Style = (Style)Application.Current.FindResource(okStyle), IsDefault = true, MinWidth = 90 };
            okButton.Click += (s, e) => { w.DialogResult = true; };
            buttons.Children.Add(okButton);
            if (cancel != null)
            {
                var cancelButton = new Button { Content = cancel, IsCancel = true, MinWidth = 90 };
                buttons.Children.Add(cancelButton);
            }
            panel.Children.Add(buttons);
            w.Content = panel;
            return w.ShowDialog() == true;
        }
    }
}
