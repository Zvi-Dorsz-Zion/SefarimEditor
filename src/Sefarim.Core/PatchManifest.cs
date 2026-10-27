using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sefarim.Core
{
    /// <summary>קובץ הפרשים אחד שבמניפסט, עם הגודל והחתימות שלו לפני ואחרי פריסה.</summary>
    public sealed class PatchFileEntry
    {
        public string File { get; set; }
        public string Compression { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string UncompressedSha256 { get; set; }
        public long UncompressedSize { get; set; }
    }

    /// <summary>
    /// מניפסט של עדכון חלקי (patch-vX-vY.db.zst.manifest.json), כמו DeltaManifest באוצריא.
    /// שדה חובה חסר או לא תקין – שגיאה, כדי שלא נחיל עדכון שאנחנו לא מבינים.
    /// </summary>
    public sealed class PatchManifest
    {
        public int FromVersion { get; set; }
        public int ToVersion { get; set; }
        public int FromSchemaVersion { get; set; }
        public int ToSchemaVersion { get; set; }
        public int? PatchFormatVersion { get; set; }
        public string FromContentHash { get; set; }
        public string ToContentHash { get; set; }
        public List<PatchFileEntry> PatchFiles { get; set; } = new List<PatchFileEntry>();
        public Dictionary<string, string> FromTableContentHashes { get; set; }
        public Dictionary<string, string> ToTableContentHashes { get; set; }

        /// <summary>"מחסום" של מעבר סכמה: לא עדכון אמיתי, מחייב הורדה מלאה.</summary>
        public bool FullRebase { get; set; }

        public long CompressedSize => PatchFiles.Sum(f => f.Size);
        public long UncompressedSize => PatchFiles.Sum(f => f.UncompressedSize);

        public static PatchManifest Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) throw new FormatException("המניפסט אינו אובייקט JSON.");
                var m = new PatchManifest
                {
                    FromVersion = RequireInt(r, "fromVersion"),
                    ToVersion = RequireInt(r, "toVersion"),
                    FromSchemaVersion = RequireInt(r, "fromSchemaVersion"),
                    ToSchemaVersion = RequireInt(r, "toSchemaVersion"),
                    PatchFormatVersion = OptionalInt(r, "patchFormatVersion"),
                    FromContentHash = RequireString(r, "fromContentHash"),
                    ToContentHash = RequireString(r, "toContentHash"),
                    FromTableContentHashes = OptionalMap(r, "fromTableContentHashes"),
                    ToTableContentHashes = OptionalMap(r, "toTableContentHashes"),
                    FullRebase = r.TryGetProperty("fullRebase", out var fr) && fr.ValueKind == JsonValueKind.True,
                };
                if (!r.TryGetProperty("patchFiles", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                    throw new FormatException("במניפסט אין קובצי עדכון.");
                foreach (var f in files.EnumerateArray())
                {
                    var entry = new PatchFileEntry
                    {
                        File = RequireFileName(f, "file"),
                        Compression = RequireString(f, "compression"),
                        Sha256 = RequireString(f, "sha256").ToLowerInvariant(),
                        Size = RequireLong(f, "size"),
                        UncompressedSha256 = RequireString(f, "uncompressedSha256").ToLowerInvariant(),
                        UncompressedSize = RequireLong(f, "uncompressedSize"),
                    };
                    if (entry.Compression != "zstd") throw new FormatException("סוג דחיסה לא מוכר: " + entry.Compression);
                    m.PatchFiles.Add(entry);
                }
                return m;
            }
        }

        private static JsonElement Require(JsonElement r, string key)
        {
            if (!r.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
                throw new FormatException($"חסר השדה {key} במניפסט.");
            return v;
        }

        private static int RequireInt(JsonElement r, string key) =>
            Require(r, key).TryGetInt32(out var v) ? v : throw new FormatException($"השדה {key} במניפסט אינו מספר.");

        private static long RequireLong(JsonElement r, string key) =>
            Require(r, key).TryGetInt64(out var v) && v >= 0 ? v : throw new FormatException($"השדה {key} במניפסט אינו מספר תקין.");

        private static int? OptionalInt(JsonElement r, string key) =>
            r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null;

        private static string RequireString(JsonElement r, string key)
        {
            var v = Require(r, key);
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrEmpty(s)) throw new FormatException($"השדה {key} במניפסט ריק.");
            return s;
        }

        /// <summary>שם קובץ פשוט בלבד: נתיב היה מאפשר כתיבה מחוץ לתיקיית ההורדה.</summary>
        private static string RequireFileName(JsonElement r, string key)
        {
            var s = RequireString(r, key);
            if (s != Path.GetFileName(s) || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s == "." || s == "..")
                throw new FormatException("שם קובץ לא תקין במניפסט: " + s);
            return s;
        }

        private static Dictionary<string, string> OptionalMap(JsonElement r, string key)
        {
            if (!r.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.Object) throw new FormatException($"השדה {key} במניפסט אינו מפה.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in v.EnumerateObject())
            {
                var s = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (string.IsNullOrEmpty(s)) throw new FormatException($"ערך ריק בשדה {key} במניפסט.");
                map[p.Name] = s;
            }
            return map;
        }
    }
}
