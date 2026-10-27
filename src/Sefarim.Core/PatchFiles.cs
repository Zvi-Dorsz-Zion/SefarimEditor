using System;
using System.IO;
using System.Security.Cryptography;
using ZstdSharp;

namespace Sefarim.Core
{
    /// <summary>אימות ופריסה של קובצי עדכון לפני שנוגעים בספרייה.</summary>
    public static class PatchFiles
    {
        /// <summary>מוודא שהקובץ בגודל ובחתימה שבמניפסט. אחרת – שגיאה.</summary>
        public static void Verify(string path, long size, string sha256, string what)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(what + " לא נמצא.", path);
            if (info.Length != size)
                throw new InvalidDataException($"{what}: הגודל ({info.Length:N0}) אינו הגודל שבמניפסט ({size:N0}).");
            if (!string.Equals(Sha256Of(path), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(what + ": החתימה אינה תואמת למניפסט. ייתכן שההורדה נפגמה.");
        }

        public static string Sha256Of(string path)
        {
            using (var sha = new SHA256Cng())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// בודק את הקובץ הדחוס, פורס אותו לקובץ זמני ובודק גם את התוצאה. רק אחרי שהכול תקין
        /// הקובץ מקבל את שמו הסופי, כך שקובץ פרוס חלקי לעולם לא נראה תקין.
        /// </summary>
        public static string Prepare(string compressedPath, PatchFileEntry entry, IProgress<string> progress = null)
        {
            progress?.Report("בודק את קובץ העדכון שהורד…");
            Verify(compressedPath, entry.Size, entry.Sha256, "קובץ העדכון");
            var target = Path.Combine(Path.GetDirectoryName(compressedPath), Path.GetFileNameWithoutExtension(compressedPath));
            if (File.Exists(target))
            {
                try
                {
                    Verify(target, entry.UncompressedSize, entry.UncompressedSha256, "קובץ העדכון הפרוס");
                    return target;
                }
                catch (InvalidDataException) { File.Delete(target); }
            }
            var temp = target + ".partial";
            progress?.Report("פורס את קובץ העדכון…");
            using (var input = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var z = new DecompressionStream(input))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                z.CopyTo(output, 1 << 20);
            Verify(temp, entry.UncompressedSize, entry.UncompressedSha256, "קובץ העדכון הפרוס");
            File.Move(temp, target);
            return target;
        }
    }
}
