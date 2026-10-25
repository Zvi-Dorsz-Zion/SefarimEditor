using System;
using System.Collections.Generic;
using System.Text;
using ZstdSharp;

namespace Sefarim.Core
{
    /// <summary>פענוח טקסט השורות של אוצריא, שדחוס ב-zstd עם מילון מטבלת zstd_dict.</summary>
    internal sealed class ZstdText : IDisposable
    {
        private readonly SeforimDb _db;
        private readonly Dictionary<long, Decompressor> _byDict = new Dictionary<long, Decompressor>();

        public ZstdText(SeforimDb db) => _db = db;

        public static bool IsZstd(byte[] b) =>
            b.Length >= 4 && b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD;

        public string Decode(byte[] frame)
        {
            var dictId = DictionaryId(frame);
            if (!_byDict.TryGetValue(dictId, out var d))
            {
                d = new Decompressor();
                if (dictId != 0)
                {
                    var dict = _db.Scalar("SELECT dict FROM zstd_dict WHERE id=$id", ("$id", dictId)) as byte[];
                    if (dict == null) throw new InvalidOperationException($"מילון zstd מספר {dictId} חסר בקובץ.");
                    d.LoadDictionary(dict);
                }
                _byDict[dictId] = d;
            }
            return Encoding.UTF8.GetString(d.Unwrap(frame).ToArray());
        }

        /// <summary>קורא את מזהה המילון מכותרת המסגרת של zstd (0 = בלי מילון).</summary>
        private static long DictionaryId(byte[] f)
        {
            var descriptor = f[4];
            var dictFlag = descriptor & 0x3;
            var singleSegment = (descriptor & 0x20) != 0;
            var pos = 5 + (singleSegment ? 0 : 1);
            switch (dictFlag)
            {
                case 1: return f[pos];
                case 2: return BitConverter.ToUInt16(f, pos);
                case 3: return BitConverter.ToUInt32(f, pos);
                default: return 0;
            }
        }

        public void Dispose()
        {
            foreach (var d in _byDict.Values) d.Dispose();
            _byDict.Clear();
        }
    }
}
