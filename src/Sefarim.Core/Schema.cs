using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Sefarim.Core
{
    public sealed class ColumnInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int PkOrder { get; set; }
    }

    public enum DeleteAction
    {
        Cascade,
        SetNull,
        Block,
    }

    public sealed class ForeignKeyInfo
    {
        public string Table { get; set; }
        public string Column { get; set; }
        public string RefTable { get; set; }
        public string RefColumn { get; set; }
        public DeleteAction OnDelete { get; set; }

        /// <summary>true כשהקשר לא מוצהר בסכמה ונגזר מעמודה בשם bookId.</summary>
        public bool Inferred { get; set; }
    }

    public sealed class TableInfo
    {
        public string Name { get; set; }
        public List<ColumnInfo> Columns { get; } = new List<ColumnInfo>();
        public bool WithoutRowid { get; set; }

        /// <summary>
        /// עמודות המפתח. טבלה בלי מפתח מוצהר מזוהה לפי כל העמודות שלה, ולא לפי
        /// rowid, כי VACUUM עשוי למספר rowid מחדש.
        /// </summary>
        public List<string> Key { get; } = new List<string>();

        /// <summary>מפתח של עמודה אחת שאינה יכולה להיות NULL, שאפשר לחפש לפיו ב-IN פשוט.</summary>
        public bool SimpleKey => Key.Count == 1 && (WithoutRowid || Columns.Any(c => c.Name == Key[0] && c.PkOrder > 0 && c.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)));

        public IEnumerable<string> ColumnNames => Columns.Select(c => c.Name);

        public bool HasColumn(string name) =>
            Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class SchemaInfo
    {
        public Dictionary<string, TableInfo> Tables { get; } =
            new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        public List<ForeignKeyInfo> ForeignKeys { get; } = new List<ForeignKeyInfo>();

        public List<string> Triggers { get; } = new List<string>();

        public TableInfo this[string name] => Tables.TryGetValue(name, out var t) ? t : null;

        public bool Has(string table) => Tables.ContainsKey(table);

        public static SchemaInfo Read(SqliteConnection conn, string schemaName = "main")
        {
            var info = new SchemaInfo();
            var names = new List<(string name, string sql)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT type, name, sql FROM {schemaName}.sqlite_master " +
                                  "WHERE type IN ('table','trigger') AND name NOT LIKE 'sqlite_%'";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (r.GetString(0) == "trigger") info.Triggers.Add(r.GetString(1));
                        else names.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
                    }
                }
            }

            foreach (var (name, sql) in names)
            {
                var t = new TableInfo
                {
                    Name = name,
                    WithoutRowid = sql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) >= 0,
                };
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA {schemaName}.table_info({Sql.Q(name)})";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            t.Columns.Add(new ColumnInfo
                            {
                                Name = r.GetString(1),
                                Type = r.IsDBNull(2) ? "" : r.GetString(2),
                                PkOrder = r.GetInt32(5),
                            });
                        }
                    }
                }
                t.Key.AddRange(t.Columns.Where(c => c.PkOrder > 0).OrderBy(c => c.PkOrder).Select(c => c.Name));
                if (t.Key.Count == 0) t.Key.AddRange(t.Columns.Select(c => c.Name));
                info.Tables[name] = t;
            }

            foreach (var t in info.Tables.Values)
            {
                var groups = new Dictionary<long, List<(string from, string table, string to, string onDelete)>>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA {schemaName}.foreign_key_list({Sql.Q(t.Name)})";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var id = r.GetInt64(0);
                            if (!groups.TryGetValue(id, out var list)) groups[id] = list = new List<(string, string, string, string)>();
                            list.Add((r.GetString(3), r.GetString(2), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(6)));
                        }
                    }
                }
                foreach (var g in groups.Values)
                {
                    if (g.Count != 1)
                        throw new NotSupportedException($"מפתח זר מרובה עמודות בטבלה {t.Name} אינו נתמך.");
                    var (from, refTable, to, onDelete) = g[0];
                    var parent = info[refTable];
                    if (to == null)
                    {
                        if (parent == null || parent.Key.Count != 1)
                            throw new NotSupportedException($"לא ניתן לזהות את יעד הקשר {t.Name}.{from}.");
                        to = parent.Key[0];
                    }
                    info.ForeignKeys.Add(new ForeignKeyInfo
                    {
                        Table = t.Name,
                        Column = from,
                        RefTable = parent?.Name ?? refTable,
                        RefColumn = to,
                        OnDelete = ParseAction(onDelete),
                    });
                }
            }

            // טבלאות עם עמודת bookId בלי קשר מוצהר (למשל line_ref, line_dh) שייכות לספר.
            if (info.Has("book"))
            {
                foreach (var t in info.Tables.Values)
                {
                    if (t.Name.Equals("book", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!t.HasColumn("bookId")) continue;
                    var col = t.Columns.First(c => c.Name.Equals("bookId", StringComparison.OrdinalIgnoreCase)).Name;
                    if (info.ForeignKeys.Any(f => f.Table == t.Name && f.Column == col)) continue;
                    info.ForeignKeys.Add(new ForeignKeyInfo
                    {
                        Table = t.Name,
                        Column = col,
                        RefTable = info["book"].Name,
                        RefColumn = "id",
                        OnDelete = DeleteAction.Cascade,
                        Inferred = true,
                    });
                }
            }
            return info;
        }

        private static DeleteAction ParseAction(string s)
        {
            switch ((s ?? "").Trim().ToUpperInvariant())
            {
                case "CASCADE": return DeleteAction.Cascade;
                case "SET NULL": return DeleteAction.SetNull;
                default: return DeleteAction.Block;
            }
        }
    }
}
