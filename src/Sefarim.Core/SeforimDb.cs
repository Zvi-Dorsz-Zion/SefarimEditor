using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sefarim.Core
{
    public sealed class CategoryNode
    {
        public long Id { get; set; }
        public long? ParentId { get; set; }
        public string Title { get; set; }
        public int OrderIndex { get; set; }
        public CategoryNode Parent { get; set; }
        public List<CategoryNode> Children { get; } = new List<CategoryNode>();
        public List<BookInfo> Books { get; } = new List<BookInfo>();

        public int TotalBooks => Books.Count + Children.Sum(c => c.TotalBooks);

        /// <summary>נתיב הקטגוריה מהשורש, למשל "תלמוד/בבלי/סדר זרעים".</summary>
        public string Path => Parent == null ? Title : Parent.Path + "/" + Title;

        public IEnumerable<BookInfo> AllBooks() => Books.Concat(Children.SelectMany(c => c.AllBooks()));

        public IEnumerable<CategoryNode> SelfAndDescendants() =>
            new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));
    }

    public sealed class BookInfo
    {
        public long Id { get; set; }
        public long CategoryId { get; set; }
        public string Title { get; set; }
        public string ShortDesc { get; set; }
        public int OrderIndex { get; set; }
        public long TotalLines { get; set; }
        public string Authors { get; set; }
        public CategoryNode Category { get; set; }

        /// <summary>שורת משנה לתצוגה: מחבר וקטגוריה.</summary>
        public string Subtitle => string.Join("  ·  ", new[] { Authors, Category?.Title }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public sealed class Catalog
    {
        public List<CategoryNode> Roots { get; } = new List<CategoryNode>();
        public Dictionary<long, CategoryNode> Categories { get; } = new Dictionary<long, CategoryNode>();
        public Dictionary<long, BookInfo> Books { get; } = new Dictionary<long, BookInfo>();
    }

    public sealed class BookDetails
    {
        public BookInfo Book { get; set; }
        public string Source { get; set; }
        public string HeRef { get; set; }
        public string ShortDesc { get; set; }
        public string LongDesc { get; set; }
        public bool HasLongDesc { get; set; }
        public List<string> Authors { get; } = new List<string>();
        public List<string> Topics { get; } = new List<string>();
        public List<string> BaseBooks { get; } = new List<string>();
    }

    /// <summary>חיבור לקובץ seforim.db של אוצריא או של זית.</summary>
    public sealed class SeforimDb : IDisposable
    {
        private SchemaInfo _schema;
        private ZstdText _zstd;

        public string FilePath { get; }
        public bool IsReadOnly { get; }
        public SqliteConnection Connection { get; }

        public SchemaInfo Schema => _schema ?? (_schema = SchemaInfo.Read(Connection));

        /// <summary>באוצריא הטקסט דחוס בטבלה נפרדת; בזית הוא בעמודה line.content.</summary>
        public bool CompressedText => Schema.Has("line_content");

        private SeforimDb(string path, bool readOnly)
        {
            SqliteInit.Ensure();
            FilePath = path;
            IsReadOnly = readOnly;
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            Connection = new SqliteConnection(csb.ToString());
            Connection.Open();
            Exec("PRAGMA foreign_keys=OFF");
            Exec("PRAGMA busy_timeout=5000");
        }

        public static SeforimDb Open(string path, bool readOnly = true)
        {
            var db = new SeforimDb(path, readOnly);
            try
            {
                if (!db.Schema.Has("book") || !db.Schema.Has("category") || !db.Schema.Has("line"))
                    throw new InvalidOperationException("הקובץ אינו ספרייה של אוצריא או של זית.");
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _zstd?.Dispose();
            Connection.Dispose();
        }

        public int Exec(string sql, params (string name, object value)[] args)
        {
            using (var cmd = Command(sql, args)) return cmd.ExecuteNonQuery();
        }

        public object Scalar(string sql, params (string name, object value)[] args)
        {
            using (var cmd = Command(sql, args))
            {
                var v = cmd.ExecuteScalar();
                return v is DBNull ? null : v;
            }
        }

        private SqliteTransaction _tx;
        private int _depth;

        /// <summary>
        /// פותח טרנזקציה (BEGIN IMMEDIATE). כל הפקודות עד הסגירה רצות בתוכה.
        /// בתוך טרנזקציה פתוחה נפתחת נקודת שמירה (SAVEPOINT): הסגירה שלה רק משחררת אותה,
        /// והכול נכתב בפועל רק כשהטרנזקציה החיצונית נסגרת. כך עדכון ספרייה והחלה מחדש
        /// של השינויים נעשים כפעולה אחת.
        /// </summary>
        public Transaction Begin()
        {
            if (_tx == null)
            {
                _tx = Connection.BeginTransaction();
                return new Transaction(this, null);
            }
            var name = "sefarim_sp" + (++_depth);
            _tx.Save(name);
            return new Transaction(this, name);
        }

        public bool InTransaction => _tx != null;

        public sealed class Transaction : IDisposable
        {
            private readonly SeforimDb _owner;
            private readonly string _savepoint;
            private bool _done;
            private bool _disposed;

            internal Transaction(SeforimDb owner, string savepoint)
            {
                _owner = owner;
                _savepoint = savepoint;
            }

            public void Commit()
            {
                if (_savepoint == null) _owner._tx.Commit();
                else _owner._tx.Release(_savepoint);
                _done = true;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_savepoint != null)
                {
                    if (!_done)
                    {
                        _owner._tx.Rollback(_savepoint);
                        _owner._tx.Release(_savepoint);
                    }
                    _owner._depth--;
                    return;
                }
                if (!_done)
                {
                    try { _owner._tx.Rollback(); }
                    catch (SqliteException) { }
                }
                _owner._tx.Dispose();
                _owner._tx = null;
                _owner._depth = 0;
            }
        }

        /// <summary>אחרי שינוי מבנה (עדכון ספרייה): קוראים מחדש את מבנה הטבלאות ואת מילון הדחיסה.</summary>
        public void InvalidateCaches()
        {
            _schema = null;
            _zstd?.Dispose();
            _zstd = null;
        }

        public SqliteCommand Command(string sql, params (string name, object value)[] args)
        {
            var cmd = Connection.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        public string GetMeta(string key)
        {
            if (!Schema.Has("schema_meta")) return null;
            return Scalar("SELECT value FROM schema_meta WHERE key=$k", ("$k", key)) as string;
        }

        public int? DbVersion => int.TryParse(GetMeta("db_version"), out var v) ? v : (int?)null;

        public int? SchemaVersion => int.TryParse(GetMeta("db_schema_version"), out var v) ? v : (int?)null;

        public Catalog LoadCatalog()
        {
            var cat = new Catalog();
            using (var cmd = Command("SELECT id, parentId, title, orderIndex FROM category"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var node = new CategoryNode
                    {
                        Id = r.GetInt64(0),
                        ParentId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                        Title = r.IsDBNull(2) ? "" : r.GetString(2),
                        OrderIndex = r.IsDBNull(3) ? 999 : r.GetInt32(3),
                    };
                    cat.Categories[node.Id] = node;
                }
            }
            foreach (var node in cat.Categories.Values)
            {
                if (node.ParentId.HasValue && cat.Categories.TryGetValue(node.ParentId.Value, out var parent))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else cat.Roots.Add(node);
            }

            var authors = new Dictionary<long, List<string>>();
            if (Schema.Has("book_author") && Schema.Has("author"))
            {
                using (var cmd = Command("SELECT ba.bookId, a.name FROM book_author ba JOIN author a ON a.id = ba.authorId"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var id = r.GetInt64(0);
                        if (!authors.TryGetValue(id, out var list)) authors[id] = list = new List<string>();
                        list.Add(r.GetString(1));
                    }
                }
            }

            var hasShort = Schema["book"].HasColumn("heShortDesc");
            using (var cmd = Command("SELECT id, categoryId, title, orderIndex, totalLines" +
                                     (hasShort ? ", heShortDesc" : ", NULL") + " FROM book"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var b = new BookInfo
                    {
                        Id = r.GetInt64(0),
                        CategoryId = r.GetInt64(1),
                        Title = r.IsDBNull(2) ? "" : r.GetString(2),
                        OrderIndex = r.IsDBNull(3) ? 999 : r.GetInt32(3),
                        TotalLines = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                        ShortDesc = r.IsDBNull(5) ? null : r.GetString(5),
                    };
                    b.Authors = authors.TryGetValue(b.Id, out var list) ? string.Join(", ", list) : "";
                    cat.Books[b.Id] = b;
                    if (cat.Categories.TryGetValue(b.CategoryId, out var node))
                    {
                        b.Category = node;
                        node.Books.Add(b);
                    }
                }
            }

            var heb = CultureInfo.GetCultureInfo("he-IL").CompareInfo;
            void Sort(CategoryNode n)
            {
                n.Children.Sort((a, b) => a.OrderIndex != b.OrderIndex ? a.OrderIndex.CompareTo(b.OrderIndex) : heb.Compare(a.Title, b.Title));
                n.Books.Sort((a, b) => a.OrderIndex != b.OrderIndex ? a.OrderIndex.CompareTo(b.OrderIndex) : heb.Compare(a.Title, b.Title));
                foreach (var c in n.Children) Sort(c);
            }
            cat.Roots.Sort((a, b) => a.OrderIndex != b.OrderIndex ? a.OrderIndex.CompareTo(b.OrderIndex) : heb.Compare(a.Title, b.Title));
            foreach (var r in cat.Roots) Sort(r);
            return cat;
        }

        public BookDetails LoadDetails(BookInfo book)
        {
            var d = new BookDetails { Book = book };
            var bookTable = Schema["book"];
            d.HasLongDesc = bookTable.HasColumn("heDesc");
            var cols = "heRef, " + (bookTable.HasColumn("heShortDesc") ? "heShortDesc" : "NULL") + ", " +
                       (d.HasLongDesc ? "heDesc" : "NULL") + ", sourceId";
            using (var cmd = Command($"SELECT {cols} FROM book WHERE id=$id", ("$id", book.Id)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    d.HeRef = r.IsDBNull(0) ? null : r.GetString(0);
                    d.ShortDesc = r.IsDBNull(1) ? null : r.GetString(1);
                    d.LongDesc = r.IsDBNull(2) ? null : r.GetString(2);
                    if (!r.IsDBNull(3) && Schema.Has("source"))
                        d.Source = Scalar("SELECT name FROM source WHERE id=$s", ("$s", r.GetInt64(3))) as string;
                }
            }
            if (Schema.Has("book_author"))
                d.Authors.AddRange(Strings("SELECT a.name FROM book_author ba JOIN author a ON a.id=ba.authorId WHERE ba.bookId=$id", book.Id));
            if (Schema.Has("book_topic") && Schema.Has("topic"))
                d.Topics.AddRange(Strings("SELECT t.name FROM book_topic bt JOIN topic t ON t.id=bt.topicId WHERE bt.bookId=$id", book.Id));
            if (Schema.Has("book_base_text"))
                d.BaseBooks.AddRange(Strings("SELECT b.title FROM book_base_text bb JOIN book b ON b.id=bb.baseBookId WHERE bb.bookId=$id", book.Id));
            return d;
        }

        public List<string> AllAuthors() =>
            Schema.Has("author") ? Strings("SELECT name FROM author ORDER BY name", null) : new List<string>();

        private List<string> Strings(string sql, long? id)
        {
            var list = new List<string>();
            using (var cmd = id.HasValue ? Command(sql, ("$id", id.Value)) : Command(sql))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            }
            return list;
        }

        /// <summary>קורא שורות מתוך ספר, כטקסט נקי מתגיות HTML.</summary>
        public List<string> ReadLines(long bookId, int from, int count)
        {
            var lines = new List<string>();
            var sql = CompressedText
                ? "SELECT c.content FROM line l JOIN line_content c ON c.id = l.id WHERE l.bookId=$b AND l.lineIndex >= $f ORDER BY l.lineIndex LIMIT $n"
                : "SELECT content FROM line WHERE bookId=$b AND lineIndex >= $f ORDER BY lineIndex LIMIT $n";
            using (var cmd = Command(sql, ("$b", bookId), ("$f", from), ("$n", count)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    if (r.IsDBNull(0)) { lines.Add(""); continue; }
                    var v = r.GetValue(0);
                    string text;
                    if (v is byte[] bytes)
                        text = ZstdText.IsZstd(bytes) ? Zstd.Decode(bytes) : Encoding.UTF8.GetString(bytes);
                    else
                        text = Convert.ToString(v, CultureInfo.InvariantCulture);
                    lines.Add(StripHtml(text));
                }
            }
            return lines;
        }

        private ZstdText Zstd => _zstd ?? (_zstd = new ZstdText(this));

        private static readonly Regex TagRx = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static string StripHtml(string html) =>
            System.Net.WebUtility.HtmlDecode(TagRx.Replace(html ?? "", "")).Trim();
    }
}
