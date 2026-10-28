using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sefarim.Core
{
    /// <summary>טקסט של ספר שמוכן להוספה: שורה לכל שורה, כותרות בתגיות &lt;h1&gt;…&lt;h6&gt;.</summary>
    public sealed class BookText
    {
        public List<string> Lines { get; set; }
        public int Headings { get; set; }

        /// <summary>בקובץ לא היו כותרות בכלל, ולכן נוספה בראשו כותרת בשם הספר (כמו בספרים של אוצריא וזית).</summary>
        public bool AddedTitleHeading { get; set; }

        public string EncodingName { get; set; }
        public bool HasNekudot { get; set; }
        public bool HasTeamim { get; set; }
    }

    /// <summary>
    /// קריאת קובץ טקסט של ספר והכנתו להוספה לזית. החישובים מועתקים מהמחולל של זית
    /// (SeforimLibrary, generator/otzariasqlite/Generator.kt ו-HtmlCharCounter.kt), כדי שספר
    /// שנוסף ייראה ויתנהג כמו ספר רשמי.
    /// </summary>
    public static class BookImport
    {
        /// <summary>מזהים מהמספר הזה ומעלה שמורים לספרים שנוספו, כדי שלא יתנגשו בספרים רשמיים.</summary>
        public const long ReservedIdBase = 900_000_000;

        /// <summary>מגבלה סבירה, כדי שקובץ שנבחר בטעות (למשל קובץ ענק שאינו ספר) לא ייטען.</summary>
        public const long MaxFileBytes = 200L * 1024 * 1024;

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static BookText Read(byte[] bytes, string title)
        {
            if (bytes.LongLength > MaxFileBytes) throw new InvalidOperationException("הקובץ גדול מדי (מעל 200MB).");
            var (text, encoding) = Decode(bytes);
            if (text.IndexOf('\0') >= 0) throw new InvalidOperationException("הקובץ אינו קובץ טקסט (יש בו תווים בינאריים).");
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) throw new InvalidOperationException("הקובץ ריק.");

            var result = new BookText { Lines = lines, EncodingName = encoding };
            result.Headings = lines.Count(l => HeadingLevel(l) > 0 && HeadingText(l).Length > 0);
            if (result.Headings == 0)
            {
                lines.Insert(0, "<h1>" + System.Net.WebUtility.HtmlEncode(title) + "</h1>");
                result.Headings = 1;
                result.AddedTitleHeading = true;
            }
            foreach (var l in lines)
            {
                foreach (var c in l)
                {
                    if (IsTeamim(c)) result.HasTeamim = true;
                    else if (IsNikud(c)) result.HasNekudot = true;
                }
                if (result.HasTeamim && result.HasNekudot) break;
            }
            return result;
        }

        /// <summary>UTF-8 (עם או בלי BOM), UTF-16 עם BOM, ואם הקובץ אינו UTF-8 תקין – קידוד עברית של Windows (1255).</summary>
        private static (string text, string encoding) Decode(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return (new UTF8Encoding(false, true).GetString(b, 3, b.Length - 3), "UTF-8");
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "UTF-16");
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "UTF-16");
            try
            {
                return (new UTF8Encoding(false, true).GetString(b), "UTF-8");
            }
            catch (DecoderFallbackException)
            {
                return (Encoding.GetEncoding(1255).GetString(b), "Windows-1255");
            }
        }

        /// <summary>רמת הכותרת לפי תחילת השורה, בדיוק כמו detectHeaderLevel בזית. 0 = שורה רגילה.</summary>
        public static int HeadingLevel(string line)
        {
            for (var n = 1; n <= 6; n++)
                if (line.StartsWith("<h" + n, StringComparison.OrdinalIgnoreCase)) return n;
            return 0;
        }

        private static readonly Regex TagRx = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex SpacesRx = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// טקסט הכותרת לתוכן העניינים, כמו cleanHtml בזית: בלי תגיות, רווחים מצומצמים, מקף עברי
        /// הופך לרווח, ובלי ניקוד וטעמים.
        /// </summary>
        public static string HeadingText(string line)
        {
            var s = System.Net.WebUtility.HtmlDecode(TagRx.Replace(line ?? "", ""));
            s = SpacesRx.Replace(s.Trim(), " ");
            s = s.Replace('־', ' ');
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (!IsNikud(c) && !IsTeamim(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>מספר התווים הנראים, העתק של countVisibleChars בזית (משמש לגודל פס הגלילה).</summary>
        public static int VisibleChars(string html)
        {
            if (string.IsNullOrEmpty(html)) return 0;
            var count = 0;
            var inTag = false;
            var i = 0;
            var len = html.Length;
            while (i < len)
            {
                var c = html[i];
                if (inTag)
                {
                    if (c == '>') inTag = false;
                }
                else if (c == '<') inTag = true;
                else if (c == '&')
                {
                    // ישות (&...;) נחשבת לתו אחד; בלי ';' בתוך 10 תווים – רק ה-& עצמו.
                    var end = Math.Min(len, i + 10);
                    var j = i + 1;
                    var terminated = false;
                    while (j < end)
                    {
                        if (html[j] == ';') { terminated = true; break; }
                        j++;
                    }
                    count++;
                    if (terminated) i = j;
                }
                else if (!IsZeroWidth(c)) count++;
                i++;
            }
            return count;
        }

        private static bool IsZeroWidth(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format;
        }

        /// <summary>isNikudChar של זית (משמש גם להסרת ניקוד מכותרות וגם לדגל hasNekudot).</summary>
        private static bool IsNikud(char c) => (c >= 'ְ' && c <= 'ֽ') || c == 'ׁ' || c == 'ׂ' || c == 'ׇ';

        private static bool IsTeamim(char c) => c >= '֑' && c <= '֯';

        /// <summary>שם ברירת מחדל לספר לפי שם הקובץ.</summary>
        public static string TitleFromFileName(string path) => Path.GetFileNameWithoutExtension(path)?.Trim() ?? "";
    }
}
