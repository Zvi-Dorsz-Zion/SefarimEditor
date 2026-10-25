using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sefarim.Core;

namespace SefarimEditor
{
    /// <summary>חלון לבחירת קטגוריית יעד (להעברת ספר).</summary>
    internal sealed class CategoryPicker : Window
    {
        private readonly TreeView _tree = new TreeView();

        public CategoryNode Selected { get; private set; }

        public CategoryPicker(Window owner, IEnumerable<CategoryNode> roots, CategoryNode current)
        {
            Title = "בחירת קטגוריה";
            Owner = owner;
            Width = 460;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Style = (Style)Application.Current.FindResource("AppWindow");
            Background = (Brush)Application.Current.FindResource("Surface");

            var dock = new DockPanel { Margin = new Thickness(16) };
            var head = new TextBlock { Text = "לאיזו קטגוריה להעביר את הספר?", Style = (Style)Application.Current.FindResource("H2") };
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var ok = new Button { Content = "בחר", Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, IsEnabled = false, MinWidth = 90 };
            ok.Click += (s, e) => { DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "ביטול", IsCancel = true, MinWidth = 90 });
            dock.Children.Add(buttons);

            foreach (var r in roots) _tree.Items.Add(Build(r, current));
            _tree.SelectedItemChanged += (s, e) =>
            {
                Selected = (_tree.SelectedItem as TreeViewItem)?.Tag as CategoryNode;
                ok.IsEnabled = Selected != null && Selected != current;
            };
            _tree.MouseDoubleClick += (s, e) => { if (ok.IsEnabled) DialogResult = true; };
            dock.Children.Add(new Border { Child = _tree, BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6) });
            Content = dock;
        }

        private static TreeViewItem Build(CategoryNode node, CategoryNode current)
        {
            var item = new TreeViewItem { Header = "📁 " + node.Title, Tag = node };
            if (node == current) item.FontWeight = FontWeights.SemiBold;
            foreach (var c in node.Children) item.Items.Add(Build(c, current));
            return item;
        }
    }
}
