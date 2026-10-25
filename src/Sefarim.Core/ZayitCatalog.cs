using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Sefarim.Core
{
    /// <summary>
    /// זית מציגה את עץ הספרים מתוך catalog.pb שליד הקובץ, ולא מהמסד. כדי שמחיקות ועריכות
    /// ייראו גם שם, שומרים עותק מקורי של הקובץ בתיקיית הגיבוי, ואחרי כל שינוי בונים אותו
    /// מחדש מהמקור לפי מה שיש בפועל במסד. שדות שלא מוכרים לנו נשמרים כפי שהם.
    /// </summary>
    public static class ZayitCatalog
    {
        public const string FileName = "catalog.pb";

        private static string OriginalPath(string backupRoot) =>
            Path.Combine(backupRoot, BackupStore.FolderName, "Zayit", "catalog.original.pb");

        private static string OriginalVersionPath(string backupRoot) => OriginalPath(backupRoot) + ".version";

        /// <summary>
        /// מעדכן את catalog.pb לפי מצב המסד. כשאין שינויים מוחלים, מחזיר את המקור כפי שהוא.
        /// </summary>
        public static void Sync(LibrarySession s, string backupRoot)
        {
            if (s.Kind != LibraryKind.Zayit) return;
            var live = Path.Combine(Path.GetDirectoryName(s.DbPath) ?? "", FileName);
            if (!File.Exists(live) && !File.Exists(OriginalPath(backupRoot))) return;

            var original = OriginalPath(backupRoot);
            var versionFile = OriginalVersionPath(backupRoot);
            var version = s.Version?.ToString(CultureInfo.InvariantCulture) ?? "";
            var storedVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;

            // עותק מקורי חסר, או ששייך לגרסה קודמת (זית הורידה חבילה חדשה): הקובץ הנוכחי הוא המקור.
            if (!File.Exists(original) || storedVersion != version)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                File.Copy(live, original, overwrite: true);
                File.WriteAllText(versionFile, version);
            }

            var status = s.Status();
            byte[] result;
            if (status != LibraryStatus.Edited) result = File.ReadAllBytes(original);
            else result = Rebuild(File.ReadAllBytes(original), s.Db);

            var tmp = live + ".tmp";
            File.WriteAllBytes(tmp, result);
            if (File.Exists(live)) File.Replace(tmp, live, null);
            else File.Move(tmp, live);
        }

        /// <summary>כל הספרים שבקטלוג: מזהה ושם (לבדיקות ולתצוגה).</summary>
        public static Dictionary<long, string> ReadBooks(byte[] data)
        {
            var result = new Dictionary<long, string>();
            void Walk(Proto.Message cat)
            {
                foreach (var f in cat.Fields.Where(f => f.Num == 5))
                {
                    var t = f.Sub.Fields.FirstOrDefault(x => x.Num == 2);
                    result[(long)f.Sub.Varint(1)] = t == null ? "" : Encoding.UTF8.GetString(t.Bytes);
                }
                foreach (var f in cat.Fields.Where(f => f.Num == 6)) Walk(f.Sub);
            }
            foreach (var f in Proto.Parse(data).Fields.Where(f => f.Num == 1)) Walk(f.Sub);
            return result;
        }

        private sealed class DbBook
        {
            public string Title;
            public long CategoryId;
            public int OrderIndex;
            public List<string> Authors;
        }

        public static byte[] Rebuild(byte[] originalBytes, SeforimDb db)
        {
            var books = new Dictionary<long, DbBook>();
            using (var cmd = db.Command("SELECT id, title, categoryId, orderIndex FROM book"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    books[r.GetInt64(0)] = new DbBook { Title = r.GetString(1), CategoryId = r.GetInt64(2), OrderIndex = r.GetInt32(3), Authors = new List<string>() };
            }
            if (db.Schema.Has("book_author"))
            {
                using (var cmd = db.Command("SELECT ba.bookId, a.name FROM book_author ba JOIN author a ON a.id = ba.authorId"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        if (books.TryGetValue(r.GetInt64(0), out var b)) b.Authors.Add(r.GetString(1));
                }
            }
            var cats = new Dictionary<long, string>();
            using (var cmd = db.Command("SELECT id, title FROM category"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) cats[r.GetInt64(0)] = r.GetString(1);
            }

            var root = Proto.Parse(originalBytes);
            var moved = new List<(long target, Proto.Message book)>();
            var byId = new Dictionary<long, Proto.Message>();

            void Walk(Proto.Message cat)
            {
                var id = (long)cat.Varint(1);
                byId[id] = cat;
                if (cats.TryGetValue(id, out var title)) cat.SetString(2, title);
                foreach (var bookField in cat.Fields.Where(f => f.Num == 5).ToList())
                {
                    var book = bookField.Sub;
                    var bookId = (long)book.Varint(1);
                    if (!books.TryGetValue(bookId, out var b))
                    {
                        cat.Fields.Remove(bookField);
                        continue;
                    }
                    book.SetString(2, b.Title);
                    book.SetFloat(4, b.OrderIndex);
                    book.SetStrings(5, b.Authors);
                    if (b.CategoryId != id)
                    {
                        book.SetVarint(3, (ulong)b.CategoryId);
                        cat.Fields.Remove(bookField);
                        moved.Add((b.CategoryId, book));
                    }
                }
                foreach (var subField in cat.Fields.Where(f => f.Num == 6).ToList())
                {
                    if (!cats.ContainsKey((long)subField.Sub.Varint(1))) cat.Fields.Remove(subField);
                    else Walk(subField.Sub);
                }
            }

            foreach (var rootField in root.Fields.Where(f => f.Num == 1).ToList())
            {
                if (!cats.ContainsKey((long)rootField.Sub.Varint(1))) root.Fields.Remove(rootField);
                else Walk(rootField.Sub);
            }
            foreach (var (target, book) in moved)
                if (byId.TryGetValue(target, out var cat)) cat.Add(5, book);

            int bookCount = 0, catCount = 0;
            void Count(Proto.Message cat)
            {
                catCount++;
                bookCount += cat.Fields.Count(f => f.Num == 5);
                foreach (var f in cat.Fields.Where(f => f.Num == 6)) Count(f.Sub);
            }
            foreach (var f in root.Fields.Where(f => f.Num == 1)) Count(f.Sub);
            if (root.Has(3)) root.SetVarint(3, (ulong)bookCount);
            if (root.Has(4)) root.SetVarint(4, (ulong)catCount);
            return root.ToBytes();
        }
    }

    /// <summary>קורא וכותב protobuf באופן כללי, בלי סכמה, ושומר שדות לא מוכרים כמו שהם.</summary>
    internal static class Proto
    {
        public sealed class Field
        {
            public int Num;
            public int Wire;
            public ulong Value;
            public byte[] Bytes;
            private Message _sub;

            public Message Sub => _sub ?? (_sub = Parse(Bytes));

            public bool HasSub => _sub != null;

            public void SetSub(Message m) => _sub = m;
        }

        public sealed class Message
        {
            public List<Field> Fields { get; } = new List<Field>();

            public bool Has(int num) => Fields.Any(f => f.Num == num);

            public ulong Varint(int num) => Fields.FirstOrDefault(f => f.Num == num && f.Wire == 0)?.Value ?? 0;

            public void SetVarint(int num, ulong v)
            {
                var f = Fields.FirstOrDefault(x => x.Num == num);
                if (f == null) Fields.Add(new Field { Num = num, Wire = 0, Value = v });
                else { f.Wire = 0; f.Value = v; }
            }

            public void SetString(int num, string s)
            {
                var bytes = Encoding.UTF8.GetBytes(s ?? "");
                var f = Fields.FirstOrDefault(x => x.Num == num);
                if (f == null) Fields.Add(new Field { Num = num, Wire = 2, Bytes = bytes });
                else { f.Wire = 2; f.Bytes = bytes; }
            }

            public void SetFloat(int num, float v)
            {
                var bytes = BitConverter.GetBytes(v);
                var f = Fields.FirstOrDefault(x => x.Num == num);
                if (f == null) Fields.Add(new Field { Num = num, Wire = 5, Bytes = bytes });
                else { f.Wire = 5; f.Bytes = bytes; }
            }

            public void SetStrings(int num, IEnumerable<string> values)
            {
                var index = Fields.FindIndex(x => x.Num == num);
                Fields.RemoveAll(x => x.Num == num);
                var insert = index < 0 ? Fields.Count : Math.Min(index, Fields.Count);
                foreach (var v in values)
                    Fields.Insert(insert++, new Field { Num = num, Wire = 2, Bytes = Encoding.UTF8.GetBytes(v) });
            }

            public void Add(int num, Message m)
            {
                var f = new Field { Num = num, Wire = 2 };
                f.SetSub(m);
                // נכנס אחרי השדה האחרון מאותו מספר, כדי לשמור על סדר השדות.
                var last = Fields.FindLastIndex(x => x.Num == num);
                Fields.Insert(last < 0 ? Fields.Count : last + 1, f);
            }

            public byte[] ToBytes()
            {
                using (var ms = new MemoryStream())
                {
                    Write(ms);
                    return ms.ToArray();
                }
            }

            private void Write(Stream s)
            {
                foreach (var f in Fields)
                {
                    WriteVarint(s, ((ulong)f.Num << 3) | (uint)f.Wire);
                    switch (f.Wire)
                    {
                        case 0:
                            WriteVarint(s, f.Value);
                            break;
                        case 2:
                            var data = f.HasSub ? f.Sub.ToBytes() : f.Bytes;
                            WriteVarint(s, (ulong)data.Length);
                            s.Write(data, 0, data.Length);
                            break;
                        default:
                            s.Write(f.Bytes, 0, f.Bytes.Length);
                            break;
                    }
                }
            }
        }

        public static Message Parse(byte[] data)
        {
            var m = new Message();
            var pos = 0;
            while (pos < data.Length)
            {
                var key = ReadVarint(data, ref pos);
                var f = new Field { Num = (int)(key >> 3), Wire = (int)(key & 7) };
                switch (f.Wire)
                {
                    case 0:
                        f.Value = ReadVarint(data, ref pos);
                        break;
                    case 1:
                        f.Bytes = Slice(data, ref pos, 8);
                        break;
                    case 2:
                        var len = (int)ReadVarint(data, ref pos);
                        f.Bytes = Slice(data, ref pos, len);
                        break;
                    case 5:
                        f.Bytes = Slice(data, ref pos, 4);
                        break;
                    default:
                        throw new InvalidDataException("קובץ הקטלוג של זית בפורמט לא צפוי.");
                }
                m.Fields.Add(f);
            }
            return m;
        }

        private static byte[] Slice(byte[] data, ref int pos, int len)
        {
            if (pos + len > data.Length) throw new InvalidDataException("קובץ הקטלוג של זית קטוע.");
            var b = new byte[len];
            Buffer.BlockCopy(data, pos, b, 0, len);
            pos += len;
            return b;
        }

        private static ulong ReadVarint(byte[] data, ref int pos)
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (pos >= data.Length) throw new InvalidDataException("קובץ הקטלוג של זית קטוע.");
                var b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }

        private static void WriteVarint(Stream s, ulong v)
        {
            while (v >= 0x80)
            {
                s.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            s.WriteByte((byte)v);
        }
    }
}
