using System;
using System.Collections.Generic;
using System.Linq;

namespace Sefarim.Core
{
    /// <summary>טבלה במנגנון העדכון של אוצריא: השם, עמודות המפתח הראשי, והאם יש בה עמודות לעדכון.</summary>
    public sealed class PatchTableSpec
    {
        public string Name { get; }
        public IReadOnlyList<string> PrimaryKey { get; }

        /// <summary>false לטבלת קישור שכל עמודותיה מפתח (עדכון = DO NOTHING).</summary>
        public bool Updatable { get; }

        public PatchTableSpec(string name, string[] pk, bool updatable)
        {
            Name = name;
            PrimaryKey = pk;
            Updatable = updatable;
        }
    }

    /// <summary>
    /// הקבועים של מנגנון העדכון, מועתקים אות באות מ-patch_table_spec.dart
    /// (Otzaria/otzaria_library_updater), שמשכפל את PatchTables.kt ב-SeforimLibrary.
    /// אסור לשנות סדר: הוא קובע גם את סדר ההחלה וגם את חתימת התוכן.
    /// </summary>
    public static class PatchTables
    {
        /// <summary>סדר ההחלה לפי תלויות מפתח זר: upserts בסדר הזה, deletes בסדר ההפוך.</summary>
        public static readonly IReadOnlyList<PatchTableSpec> InFkOrder = new[]
        {
            new PatchTableSpec("source", new[] { "id" }, true),
            new PatchTableSpec("author", new[] { "id" }, true),
            new PatchTableSpec("topic", new[] { "id" }, true),
            new PatchTableSpec("pub_place", new[] { "id" }, true),
            new PatchTableSpec("pub_date", new[] { "id" }, true),
            new PatchTableSpec("connection_type", new[] { "id" }, true),
            new PatchTableSpec("tocText", new[] { "id" }, true),
            new PatchTableSpec("generation", new[] { "id" }, true),
            new PatchTableSpec("category", new[] { "id" }, true),
            new PatchTableSpec("category_closure", new[] { "ancestorId", "descendantId" }, false),
            new PatchTableSpec("book", new[] { "id" }, true),
            new PatchTableSpec("book_author", new[] { "bookId", "authorId" }, false),
            new PatchTableSpec("book_base_text", new[] { "bookId", "baseBookId" }, false),
            new PatchTableSpec("book_topic", new[] { "bookId", "topicId" }, false),
            new PatchTableSpec("book_pub_place", new[] { "bookId", "pubPlaceId" }, false),
            new PatchTableSpec("book_pub_date", new[] { "bookId", "pubDateId" }, false),
            new PatchTableSpec("book_acronym", new[] { "bookId", "term" }, false),
            new PatchTableSpec("book_generation", new[] { "bookId", "generationId" }, false),
            new PatchTableSpec("tocEntry", new[] { "id" }, true),
            new PatchTableSpec("line", new[] { "id" }, true),
            new PatchTableSpec("line_content", new[] { "id" }, true),
            new PatchTableSpec("zstd_dict", new[] { "id" }, true),
            new PatchTableSpec("line_toc", new[] { "lineId" }, true),
            new PatchTableSpec("line_ref", new[] { "bookId", "refKeyHash", "lineIndex" }, false),
            new PatchTableSpec("line_dh", new[] { "bookId", "dhText", "lineIndex" }, true),
            new PatchTableSpec("link", new[] { "id" }, true),
            new PatchTableSpec("link_anchor", new[] { "linkId", "side", "charStart" }, true),
            new PatchTableSpec("link_range", new[] { "linkId", "side" }, true),
            new PatchTableSpec("link_coverage", new[] { "lineId", "linkId", "side" }, false),
            new PatchTableSpec("link_suppressed_side", new[] { "linkId", "side" }, true),
            new PatchTableSpec("book_has_links", new[] { "bookId" }, true),
            new PatchTableSpec("book_version", new[] { "id" }, true),
            new PatchTableSpec("version_line", new[] { "versionId", "lineId" }, true),
            new PatchTableSpec("alt_toc_structure", new[] { "id" }, true),
            new PatchTableSpec("alt_toc_entry", new[] { "id" }, true),
            new PatchTableSpec("line_alt_toc", new[] { "lineId", "structureId" }, true),
            new PatchTableSpec("default_commentator", new[] { "bookId", "commentatorBookId" }, true),
            new PatchTableSpec("default_targum", new[] { "bookId", "targumBookId" }, true),
            new PatchTableSpec("banner", new[] { "id" }, true),
            new PatchTableSpec("book_banner", new[] { "bookId" }, true),
            new PatchTableSpec("book_protection", new[] { "bookId" }, true),
            new PatchTableSpec("line_divine_name", new[] { "lineId", "charStart" }, true),
            new PatchTableSpec("line_note", new[] { "lineId", "noteIndex" }, true),
            new PatchTableSpec("line_note_divine_name", new[] { "lineId", "noteIndex", "charStart" }, true),
            new PatchTableSpec("schema_meta", new[] { "key" }, true),
        };

        private static readonly string[] HashOrderSchema6 =
        {
            "source", "author", "topic", "pub_place", "pub_date", "connection_type", "generation", "category",
            "category_closure", "tocText", "book", "book_topic", "book_author", "book_base_text", "book_pub_place",
            "book_pub_date", "book_generation", "tocEntry", "line", "line_content", "zstd_dict", "line_toc", "line_ref",
            "line_dh", "link", "link_anchor", "link_range", "link_coverage", "link_suppressed_side", "book_has_links",
            "book_version", "version_line", "book_acronym", "alt_toc_structure", "alt_toc_entry", "line_alt_toc",
            "default_commentator", "default_targum", "schema_meta",
        };

        private static readonly string[] HashOrderSchema7 = HashOrderSchema6.Take(HashOrderSchema6.Length - 1)
            .Concat(new[] { "banner", "book_banner", "book_protection", "line_divine_name", "line_note", "line_note_divine_name", "schema_meta" })
            .ToArray();

        /// <summary>מהסכמה הזו שורות כל טבלה נכנסות לחתימה לפי המפתח הראשי.</summary>
        public const int PkRowOrderSchemaVersion = 7;

        /// <summary>גרסת פורמט קובץ ההפרשים הגבוהה ביותר שאנחנו יודעים להחיל.</summary>
        public const int SupportedPatchFormatVersion = 4;

        /// <summary>סדר הטבלאות בחתימה לפי גרסת הסכמה. סכמה שלא מוכרת לנו – שגיאה.</summary>
        public static IReadOnlyList<string> HashTableOrder(int schemaVersion)
        {
            switch (schemaVersion)
            {
                case 6: return HashOrderSchema6;
                case 7: return HashOrderSchema7;
                default: throw new NotSupportedException($"התוכנה לא מכירה את מבנה הספרייה בגרסה {schemaVersion}.");
            }
        }

        /// <summary>סדר השורות בחתימה: null = הכלל הישן (id, ואם אין – כל העמודות).</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> HashRowOrder(int schemaVersion)
        {
            HashTableOrder(schemaVersion);
            if (schemaVersion < PkRowOrderSchemaVersion) return null;
            return InFkOrder.ToDictionary(t => t.Name, t => t.PrimaryKey);
        }

        public static bool IsSupportedSchema(int schemaVersion) => schemaVersion == 6 || schemaVersion == 7;
    }
}
