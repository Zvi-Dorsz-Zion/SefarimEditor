using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SQLitePCL;

namespace Sefarim.Core
{
    /// <summary>תוצאת חישוב חתימה: החתימה הכוללת (כשחושבו כל הטבלאות) וחתימה לכל טבלה.</summary>
    public sealed class LogicalHashReport
    {
        /// <summary>null כשחושבה רק חלק מהטבלאות.</summary>
        public string WholeHash { get; set; }

        public Dictionary<string, string> TableHashes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, long> TableBytes { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>
    /// חתימת התוכן הלוגית של הספרייה, בדיוק כמו LogicalContentHasher של אוצריא
    /// (logical_content_hasher.dart ב-otzaria_library_updater). זו החתימה שבמניפסט של
    /// כל עדכון (fromContentHash / toContentHash / *TableContentHashes).
    /// כל בית בזרם חייב להיות זהה למקור, אחרת החתימה לא תתאים לעולם.
    /// </summary>
    public static class LogicalHasher
    {
        private const byte NullTag = 0x00;
        private const byte BlobTag = 0x01;
        private const byte NumberTag = 0x02;
        private const byte TextTag = 0x03;
        private const byte UnitSeparator = 0x1F;
        private const byte RowSeparator = 0xFF;
        private const int ColsPerMask = 20;

        /// <summary>
        /// מחשב את החתימה על החיבור הנתון (גם בתוך טרנזקציה פתוחה).
        /// only – רק הטבלאות האלה (ואז אין חתימה כוללת).
        /// </summary>
        public static LogicalHashReport Compute(SeforimDb db, int schemaVersion, ISet<string> only = null,
            Action<long> progress = null)
        {
            var tableOrder = PatchTables.HashTableOrder(schemaVersion);
            var rowOrder = PatchTables.HashRowOrder(schemaVersion);
            var report = new LogicalHashReport();
            using (var whole = only == null ? new SHA256Cng() : null)
            {
                var sink = new Sink(whole, progress);
                foreach (var table in tableOrder)
                {
                    if (only != null && !only.Contains(table)) continue;
                    using (var tableSha = new SHA256Cng())
                    {
                        sink.BeginTable(tableSha);
                        sink.Add(Encoding.UTF8.GetBytes(" table:" + table + " "));
                        var cols = ColumnsCanonical(db, table);
                        if (cols != null)
                        {
                            sink.Add(Encoding.UTF8.GetBytes("cols:" + string.Join(",", cols)));
                            sink.Add(NullTag);
                            HashRows(db, table, cols, rowOrder, sink);
                        }
                        report.TableBytes[table] = sink.EndTable();
                        tableSha.TransformFinalBlock(new byte[0], 0, 0);
                        report.TableHashes[table] = Hex(tableSha.Hash);
                    }
                }
                sink.Flush();
                progress?.Invoke(sink.Total);
                if (whole != null)
                {
                    whole.TransformFinalBlock(new byte[0], 0, 0);
                    report.WholeHash = Hex(whole.Hash);
                }
            }
            return report;
        }

        private static string Hex(byte[] h) => BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();

        /// <summary>שמות העמודות ממוינים (סדר תווים בינארי, כמו ב-Dart), או null אם הטבלה לא קיימת.</summary>
        private static List<string> ColumnsCanonical(SeforimDb db, string table)
        {
            var names = new List<string>();
            using (var cmd = db.Command($"PRAGMA main.table_info({Sql.Q(table)})"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) names.Add(r.GetString(1));
            if (names.Count == 0) return null;
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>סדר השורות בחתימה. זהה ל-hashOrderByClause במקור (כולל id בלי מרכאות).</summary>
        internal static string OrderBy(string table, List<string> cols, IReadOnlyDictionary<string, IReadOnlyList<string>> rowOrder)
        {
            if (rowOrder == null)
                return cols.Contains("id") ? "id" : string.Join(",", cols.Select(c => "\"" + c + "\""));
            var pk = rowOrder[table];
            if (!pk.All(cols.Contains))
                throw new InvalidOperationException($"עמודות המפתח של {table} לא נמצאו בקובץ.");
            return string.Join(",", pk.Select(c => "\"" + c + "\""));
        }

        private static void HashRows(SeforimDb db, string table, List<string> cols,
            IReadOnlyDictionary<string, IReadOnlyList<string>> rowOrder, Sink sink)
        {
            var masks = new List<string>();
            for (var start = 0; start < cols.Count; start += ColsPerMask)
            {
                var end = Math.Min(start + ColsPerMask, cols.Count);
                var parts = new List<string>();
                for (var i = start; i < end; i++) parts.Add($"((typeof(\"{cols[i]}\")='blob')<<{i - start})");
                masks.Add(string.Join("|", parts));
            }
            var values = cols.Select(c => $"CASE WHEN typeof(\"{c}\")='text' THEN CAST(\"{c}\" AS BLOB) ELSE \"{c}\" END");
            var sql = $"SELECT {string.Join(",", masks.Concat(values))} FROM main.\"{table}\" ORDER BY {OrderBy(table, cols, rowOrder)}";

            var handle = db.Connection.Handle;
            if (raw.sqlite3_prepare_v2(handle, sql, out sqlite3_stmt stmt) != raw.SQLITE_OK)
                throw new InvalidOperationException("שגיאה בקריאת הטבלה " + table + ": " + raw.sqlite3_errmsg(handle).utf8_to_string());
            using (stmt)
            {
                var maskCount = masks.Count;
                var colCount = cols.Count;
                var maskValues = new long[maskCount];
                int rc;
                while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                {
                    for (var m = 0; m < maskCount; m++) maskValues[m] = raw.sqlite3_column_int64(stmt, m);
                    for (var i = 0; i < colCount; i++)
                    {
                        var c = maskCount + i;
                        switch (raw.sqlite3_column_type(stmt, c))
                        {
                            case raw.SQLITE_NULL:
                                sink.Add(NullTag);
                                break;
                            case raw.SQLITE_INTEGER:
                                sink.Add(NumberTag);
                                var l = raw.sqlite3_column_int64(stmt, c);
                                if (l >= 0) sink.AddDecimal(l);
                                else sink.Add(Encoding.ASCII.GetBytes(l.ToString(CultureInfo.InvariantCulture)));
                                break;
                            case raw.SQLITE_FLOAT:
                                sink.Add(NumberTag);
                                sink.Add(Encoding.ASCII.GetBytes(JavaDoubleToString(raw.sqlite3_column_double(stmt, c))));
                                break;
                            default:
                                var isBlob = ((maskValues[i / ColsPerMask] >> (i % ColsPerMask)) & 1) == 1;
                                sink.Add(isBlob ? BlobTag : TextTag);
                                sink.Add(raw.sqlite3_column_blob(stmt, c));
                                break;
                        }
                        sink.Add(UnitSeparator);
                    }
                    sink.Add(RowSeparator);
                }
                if (rc != raw.SQLITE_DONE)
                    throw new InvalidOperationException("שגיאה בקריאת הטבלה " + table + ": " + raw.sqlite3_errmsg(handle).utf8_to_string());
            }
        }

        /// <summary>
        /// Double.toString של Java, כמו javaDoubleToString במקור: הספרות הקצרות ביותר שמחזירות
        /// את אותו מספר, וכתיב מדעי מחוץ לטווח 0.001 עד 10,000,000.
        /// </summary>
        public static string JavaDoubleToString(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            if (value == 0) return 1 / value < 0 ? "-0.0" : "0.0";
            var x = Math.Abs(value);
            string sci = null;
            for (var p = 0; p <= 16; p++)
            {
                sci = x.ToString("E" + p, CultureInfo.InvariantCulture);
                if (double.Parse(sci, CultureInfo.InvariantCulture) == x) break;
            }
            var e = sci.IndexOf('E');
            var digits = sci.Substring(0, e).Replace(".", "");
            while (digits.Length > 1 && digits.EndsWith("0")) digits = digits.Substring(0, digits.Length - 1);
            var exp = int.Parse(sci.Substring(e + 1), CultureInfo.InvariantCulture);
            string body;
            if (exp >= 0 && exp < 7)
            {
                var intLen = exp + 1;
                body = digits.Length > intLen
                    ? digits.Substring(0, intLen) + "." + digits.Substring(intLen)
                    : digits.PadRight(intLen, '0') + ".0";
            }
            else if (exp < 0 && exp >= -3)
                body = "0." + new string('0', -exp - 1) + digits;
            else
                body = digits[0] + "." + (digits.Length > 1 ? digits.Substring(1) : "0") + "E" + exp.ToString(CultureInfo.InvariantCulture);
            return value < 0 ? "-" + body : body;
        }

        /// <summary>מאגר בתים שמוזן במנות לחתימה הכוללת ולחתימת הטבלה הנוכחית.</summary>
        private sealed class Sink
        {
            private const int Capacity = 1 << 20;
            private const long ProgressInterval = 16L << 20;
            private readonly HashAlgorithm _whole;
            private readonly Action<long> _progress;
            private readonly byte[] _buf = new byte[Capacity];
            private HashAlgorithm _table;
            private int _len;
            private long _tableStart;
            private long _lastReported;

            public long Total { get; private set; }

            public Sink(HashAlgorithm whole, Action<long> progress)
            {
                _whole = whole;
                _progress = progress;
            }

            public void BeginTable(HashAlgorithm table)
            {
                Flush();
                _table = table;
                _tableStart = Total;
            }

            public long EndTable()
            {
                Flush();
                _table = null;
                return Total - _tableStart;
            }

            public void Add(byte b)
            {
                if (_len == Capacity) Flush();
                _buf[_len++] = b;
                Total++;
            }

            public void Add(byte[] bytes) => Add(new ReadOnlySpan<byte>(bytes));

            public void Add(ReadOnlySpan<byte> bytes)
            {
                if (bytes.Length >= Capacity)
                {
                    Flush();
                    var copy = bytes.ToArray();
                    Feed(copy, copy.Length);
                    Total += copy.Length;
                    return;
                }
                if (_len + bytes.Length > Capacity) Flush();
                bytes.CopyTo(new Span<byte>(_buf, _len, bytes.Length));
                _len += bytes.Length;
                Total += bytes.Length;
            }

            /// <summary>מספר אי-שלילי בספרות ASCII, בלי הקצאות.</summary>
            public void AddDecimal(long value)
            {
                var len = 1;
                for (var n = value; n >= 10; n /= 10) len++;
                if (_len + len > Capacity) Flush();
                var i = _len + len;
                var v = value;
                do
                {
                    _buf[--i] = (byte)('0' + v % 10);
                    v /= 10;
                } while (v > 0);
                _len += len;
                Total += len;
            }

            public void Flush()
            {
                if (_len == 0) return;
                Feed(_buf, _len);
                _len = 0;
            }

            private void Feed(byte[] data, int count)
            {
                _whole?.TransformBlock(data, 0, count, null, 0);
                _table?.TransformBlock(data, 0, count, null, 0);
                if (_progress != null && Total - _lastReported >= ProgressInterval)
                {
                    _lastReported = Total;
                    _progress(Total);
                }
            }
        }
    }
}
