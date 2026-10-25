using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SefarimEditor
{
    /// <summary>
    /// טקסט בעברית שמשולבים בו נתיבים ומידות. WPF ב-.NET Framework מתעלם מתווי הכיווניות
    /// LRE/PDF, ולכן כל קטע שעטוף בהם מוצג כ-Run נפרד בכיוון משמאל לימין.
    /// </summary>
    internal static class RichText
    {
        public const char Start = '‪';
        public const char End = '‬';

        public static string Ltr(string s) => Start + s + End;

        public static void Set(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(text)) return;
            var i = 0;
            while (i < text.Length)
            {
                var s = text.IndexOf(Start, i);
                if (s < 0)
                {
                    tb.Inlines.Add(new Run(text.Substring(i)));
                    break;
                }
                if (s > i) tb.Inlines.Add(new Run(text.Substring(i, s - i)));
                var e = text.IndexOf(End, s + 1);
                if (e < 0) e = text.Length;
                tb.Inlines.Add(new Run(text.Substring(s + 1, e - s - 1)) { FlowDirection = FlowDirection.LeftToRight });
                i = e + 1;
            }
        }
    }
}
