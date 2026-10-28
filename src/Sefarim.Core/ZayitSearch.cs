using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sefarim.Core
{
    public sealed class SearchSyncResult
    {
        public int Added { get; set; }
        public int Removed { get; set; }

        /// <summary>הסנכרון לא בוצע או נכשל (השינוי בקובץ עצמו כן נשמר). null = הכול תקין.</summary>
        public string Warning { get; set; }

        public bool Changed => Added + Removed > 0;
    }

    /// <summary>
    /// מסנכרן את אינדקס החיפוש של זית עם הקובץ, דרך רכיב החיפוש: ספרים שהוספנו – נכנסים לחיפוש; ספרים שמחקנו – יוצאים
    /// ממנו; ספר שמחיקתו בוטלה – חוזר. הסנכרון מחשב בכל פעם מחדש מה צריך להיות באינדקס, ולכן אפשר להריץ אותו שוב
    /// ושוב (גם אחרי כשל, וגם אחרי עדכון של זית שמביא אינדקס חדש).
    /// הטקסט מנורמל כאן בדיוק כמו ב-BuildLuceneIndex של זית (normalizeForIndexDefault).
    /// </summary>
    public static class ZayitSearch
    {
        private sealed class State
        {
            /// <summary>מזהה האינדקס (גרסת הספרייה + קובץ segments). השתנה – האינדקס הוחלף, ומתחילים מחדש.</summary>
            public string Stamp { get; set; }
            public Dictionary<long, string> Added { get; set; } = new Dictionary<long, string>();
            public List<long> Removed { get; set; } = new List<long>();
        }

        public static string IndexDir(string dbPath) => dbPath + ".lucene";

        public static string LookupDir(string dbPath) => dbPath + ".lookup.lucene";

        private static string StatePath(LibrarySession s) =>
            Path.Combine(Path.GetDirectoryName(s.Store.FilePath) ?? "", "search-state.json");

        /// <summary>האם יש מה לסנכרן בכלל (זית, רכיב מותקן, אינדקס קיים).</summary>
        public static string Unavailable(LibrarySession s)
        {
            if (s.Kind != LibraryKind.Zayit) return "סנכרון החיפוש קיים לזית בלבד.";
            if (s.Store == null || s.Engine == null) return "הקובץ פתוח לעיון בלבד.";
            if (!SearchAddon.IsInstalled) return "רכיב החיפוש אינו מותקן.";
            if (!Directory.Exists(IndexDir(s.DbPath)) || !Directory.Exists(LookupDir(s.DbPath)))
                return "אינדקס החיפוש של זית לא נמצא ליד קובץ הספרייה.";
            return null;
        }

        public static SearchSyncResult Sync(LibrarySession s, IProgress<string> progress = null)
        {
            var result = new SearchSyncResult();
            var why = Unavailable(s);
            if (why != null)
            {
                result.Warning = why;
                return result;
            }
            var db = s.Db;
            var state = LoadState(s);
            var stamp = Stamp(s);
            if (state.Stamp != stamp) state = new State();

            var present = new HashSet<long>();
            using (var cmd = db.Command("SELECT id FROM book"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) present.Add(r.GetInt64(0));

            // ספרים רשמיים שמחקנו: מהבקשה עצמה, ומהגיבוי (מחיקת קטגוריה מוחקת ספרים שלא נרשמו בבקשה).
            var deleted = new HashSet<long>();
            var backupBooks = s.Store.TableExists(BackupStore.BeforeTable("book"));
            foreach (var op in s.Ops().Where(o => (o.State == OpState.Applied || o.State == OpState.Permanent) &&
                                                  (o.Intent.Kind == OpIntent.DeleteBooks || o.Intent.Kind == OpIntent.DeleteCategories)))
            {
                foreach (var b in op.Intent.Books) deleted.Add(b.Id);
                if (!backupBooks || !op.Tables.Contains("book")) continue;
                using (var cmd = db.Command($"SELECT id FROM {BackupStore.Alias}.{Sql.Q(BackupStore.BeforeTable("book"))} WHERE op_id=$o", ("$o", op.Id)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) deleted.Add(r.GetInt64(0));
            }
            deleted.RemoveWhere(id => present.Contains(id) || id >= BookImport.ReservedIdBase);

            var toDelete = new List<long>();
            var toAdd = new List<long>();
            var signatures = new Dictionary<long, string>();
            foreach (var id in deleted.Where(id => !state.Removed.Contains(id))) toDelete.Add(id);
            foreach (var id in state.Removed.Where(present.Contains)) toAdd.Add(id); // מחיקה שבוטלה
            foreach (var id in present.Where(id => id >= BookImport.ReservedIdBase))
            {
                var sig = Signature(db, id);
                signatures[id] = sig;
                if (!state.Added.TryGetValue(id, out var old) || old != sig) toAdd.Add(id);
            }
            foreach (var id in state.Added.Keys.Where(id => !present.Contains(id))) toDelete.Add(id);

            if (toDelete.Count == 0 && toAdd.Count == 0)
            {
                if (state.Stamp == null)
                {
                    state.Stamp = stamp;
                    SaveState(s, state);
                }
                return result;
            }

            progress?.Report("מעדכן את החיפוש של זית…");
            var (code, output, error) = SearchAddon.Run("apply", stdin =>
            {
                using (var w = new StreamWriter(stdin, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" })
                {
                    w.WriteLine("INDEX\t" + Path.GetFullPath(IndexDir(s.DbPath)));
                    w.WriteLine("LOOKUP\t" + Path.GetFullPath(LookupDir(s.DbPath)));
                    foreach (var id in toDelete) w.WriteLine("DEL\t" + id);
                    foreach (var id in toAdd.Distinct()) WriteBook(w, db, id);
                }
            }, TimeSpan.FromMinutes(30));
            if (code != 0 || !output.Contains("OK "))
            {
                result.Warning = "עדכון החיפוש של זית נכשל: " + FirstLine(error.Length > 0 ? error : output);
                return result;
            }

            foreach (var id in toDelete)
            {
                state.Added.Remove(id);
                if (id < BookImport.ReservedIdBase && !state.Removed.Contains(id)) state.Removed.Add(id);
            }
            foreach (var id in toAdd)
            {
                state.Removed.Remove(id);
                if (id >= BookImport.ReservedIdBase) state.Added[id] = signatures.TryGetValue(id, out var sig) ? sig : Signature(db, id);
            }
            state.Stamp = Stamp(s);
            SaveState(s, state);
            result.Added = toAdd.Distinct().Count();
            result.Removed = toDelete.Count;
            return result;
        }

        private static string FirstLine(string s) =>
            (s ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        // ───────────── רשומות הקלט של הרכיב, כמו BuildLuceneIndex ─────────────

        private static void WriteBook(StreamWriter w, SeforimDb db, long id)
        {
            string title;
            long categoryId;
            bool isBase;
            int order;
            using (var cmd = db.Command("SELECT title, categoryId, isBaseBook, orderIndex FROM book WHERE id=$b", ("$b", id)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return;
                title = r.GetString(0);
                categoryId = r.GetInt64(1);
                isBase = r.GetInt64(2) != 0;
                order = r.GetInt32(3);
            }
            var titleSan = NormalizePostHtml(title);
            var topics = Strings(db, "book_topic", "SELECT t.name FROM book_topic bt JOIN topic t ON t.id = bt.topicId WHERE bt.bookId=$b", id)
                .Select(NormalizePostHtml).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            var acronyms = Strings(db, "book_acronym", "SELECT term FROM book_acronym WHERE bookId=$b", id)
                .Select(NormalizePostHtml).Where(t => t.Trim().Length > 0).Distinct().ToList();

            w.WriteLine($"BOOK\t{id}\t{categoryId}\t{(isBase ? 1 : 0)}\t{order}\t{Clean(title)}");
            w.WriteLine("ANC\t" + string.Join(",", Longs(db, "SELECT ancestorId FROM category_closure WHERE descendantId=$c", categoryId)));
            // מסמכי book_title: השם, השם המנורמל אם הוא שונה, ונושאים.
            w.WriteLine("TERM\t" + Clean(title));
            if (titleSan.Trim().Length > 0 && !string.Equals(titleSan, title, StringComparison.OrdinalIgnoreCase)) w.WriteLine("TERM\t" + Clean(titleSan));
            foreach (var t in topics) w.WriteLine("TERM\t" + Clean(t));
            // אינדקס השמות: השם, השם המנורמל, ראשי תיבות ונושאים.
            var look = new List<string> { title };
            if (titleSan.Trim().Length > 0) look.Add(titleSan);
            look.AddRange(acronyms);
            look.AddRange(topics);
            foreach (var t in look.Where(t => t.Trim().Length > 0)) w.WriteLine("LOOK\t" + Clean(t));

            var count = 0;
            using (var cmd = db.Command("SELECT id, lineIndex, content FROM line WHERE bookId=$b ORDER BY lineIndex", ("$b", id)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    w.WriteLine($"LINE\t{r.GetInt64(0)}\t{r.GetInt32(1)}\t{Clean(NormalizeForIndex(r.IsDBNull(2) ? "" : r.GetString(2)))}");
                    count++;
                }
            }
            w.WriteLine("END\t" + count);
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static List<string> Strings(SeforimDb db, string table, string sql, long id)
        {
            var list = new List<string>();
            if (!db.Schema.Has(table)) return list;
            using (var cmd = db.Command(sql, ("$b", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            return list;
        }

        private static List<long> Longs(SeforimDb db, string sql, long id)
        {
            var list = new List<long>();
            using (var cmd = db.Command(sql, ("$c", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }

        /// <summary>טביעת אצבע של מה שנכנס לאינדקס מספר (שם, קטגוריה, סדר, השורות) – כדי לא לכתוב שוב ספר שלא השתנה.</summary>
        private static string Signature(SeforimDb db, long id)
        {
            var sb = new StringBuilder();
            using (var cmd = db.Command("SELECT title, categoryId, isBaseBook, orderIndex FROM book WHERE id=$b", ("$b", id)))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) sb.Append(r.GetString(0)).Append('|').Append(r.GetInt64(1)).Append('|').Append(r.GetInt64(2)).Append('|').Append(r.GetInt64(3));
            sb.Append('|').Append(string.Join(",", Longs(db, "SELECT ancestorId FROM category_closure WHERE descendantId=(SELECT categoryId FROM book WHERE id=$c)", id)));
            using (var cmd = db.Command("SELECT count(*), min(id), max(id), sum(length(content)) FROM line WHERE bookId=$b", ("$b", id)))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) for (var i = 0; i < 4; i++) sb.Append('|').Append(r.IsDBNull(i) ? "" : r.GetValue(i).ToString());
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 32);
        }

        // ───────────── מצב ─────────────

        /// <summary>מזהה של האינדקס כפי שהוא עכשיו: גרסת הספרייה וקובץ ה-segments האחרון (שם, גודל, זמן).</summary>
        private static string Stamp(LibrarySession s)
        {
            var seg = new DirectoryInfo(IndexDir(s.DbPath)).GetFiles("segments_*").OrderByDescending(f => f.Name.Length).ThenByDescending(f => f.Name).FirstOrDefault();
            return $"{s.Version}|{seg?.Name}|{seg?.Length}|{seg?.LastWriteTimeUtc.Ticks}";
        }

        private static State LoadState(LibrarySession s)
        {
            try
            {
                var path = StatePath(s);
                if (File.Exists(path)) return JsonSerializer.Deserialize<State>(File.ReadAllText(path, Encoding.UTF8)) ?? new State();
            }
            catch (JsonException) { }
            catch (IOException) { }
            return new State();
        }

        private static void SaveState(LibrarySession s, State state)
        {
            var path = StatePath(s);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // ───────────── נרמול, כמו BuildLuceneIndex.kt ─────────────

        /// <summary>normalizeForIndexDefault: Jsoup.clean(html, Safelist.none()) ואחריו normalizePostHtmlForIndex.</summary>
        public static string NormalizeForIndex(string html) => NormalizePostHtml(JsoupCleanText(html));

        /// <summary>
        /// כמו Jsoup.clean עם Safelist.none(): בלי תגיות, הישויות מפוענחות ומוחזרות בקידוד HTML (& &lt; &gt; &nbsp;).
        /// '<' נחשב תחילת תגית רק כשאחריו אות, '/', '!' או '?' (כמו במנתח של Jsoup).
        /// </summary>
        public static string JsoupCleanText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var text = new StringBuilder(html.Length);
            for (var i = 0; i < html.Length; i++)
            {
                var c = html[i];
                if (c == '<' && i + 1 < html.Length && (char.IsLetter(html[i + 1]) || html[i + 1] == '/' || html[i + 1] == '!' || html[i + 1] == '?'))
                {
                    var end = html.IndexOf('>', i + 1);
                    if (end < 0) break; // תגית שלא נסגרה: Jsoup מתעלם מהשאר
                    i = end;
                    continue;
                }
                text.Append(c);
            }
            var decoded = WebUtility.HtmlDecode(text.ToString());
            var sb = new StringBuilder(decoded.Length);
            foreach (var c in decoded)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case ' ': sb.Append("&nbsp;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>normalizePostHtmlForIndex: בלי ניקוד וטעמים, בלי גרש וגרשיים, מקף ורווחים לרווח אחד, אותיות סופיות לרגילות.</summary>
        public static string NormalizePostHtml(string text)
        {
            var sb = new StringBuilder(text?.Length ?? 0);
            var prevWasSpace = true;
            foreach (var c in text ?? "")
            {
                int code = c;
                if ((code >= 0x05B0 && code <= 0x05BD) || code == 0x05C1 || code == 0x05C2 || code == 0x05C7) continue;
                if (code >= 0x0591 && code <= 0x05AF) continue;
                if (code == 0x05F4 || code == 0x05F3) continue;
                if (code == 0x05BE || IsJavaWhitespace(c))
                {
                    if (!prevWasSpace)
                    {
                        sb.Append(' ');
                        prevWasSpace = true;
                    }
                    continue;
                }
                switch (code)
                {
                    case 0x05DA: sb.Append('כ'); break;
                    case 0x05DD: sb.Append('מ'); break;
                    case 0x05DF: sb.Append('נ'); break;
                    case 0x05E3: sb.Append('פ'); break;
                    case 0x05E5: sb.Append('צ'); break;
                    default: sb.Append(c); break;
                }
                prevWasSpace = false;
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.ToString();
        }

        /// <summary>Char.isWhitespace של Kotlin (Character.isWhitespace || isSpaceChar).</summary>
        private static bool IsJavaWhitespace(char c) => char.IsWhiteSpace(c) || char.IsSeparator(c);
    }
}
