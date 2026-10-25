using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sefarim.Core
{
    /// <summary>
    /// טביעת אצבע של שורות: סוג הערך והערך עצמו בכל תא. משמשת לוודא שמה שהוחזר
    /// לקובץ זהה בדיוק למה שגובה.
    /// </summary>
    internal static class RowHasher
    {
        public static (string hash, long rows) Hash(SqliteCommand cmd)
        {
            using (var sha = SHA256.Create())
            using (var r = cmd.ExecuteReader())
            {
                // הנתונים נצברים במאגר ומוזנים ל-SHA במנות, כי קריאה לכל תא בנפרד איטית מאוד.
                var buf = new MemoryStream(1 << 20);
                void Flush()
                {
                    sha.TransformBlock(buf.GetBuffer(), 0, (int)buf.Length, null, 0);
                    buf.SetLength(0);
                }

                long rows = 0;
                while (r.Read())
                {
                    rows++;
                    for (var i = 0; i < r.FieldCount; i++)
                    {
                        if (r.IsDBNull(i))
                        {
                            buf.WriteByte(0);
                            continue;
                        }
                        switch (r.GetValue(i))
                        {
                            case long l:
                                buf.WriteByte(2);
                                buf.Write(BitConverter.GetBytes(l), 0, 8);
                                break;
                            case double d:
                                buf.WriteByte(4);
                                buf.Write(BitConverter.GetBytes(d), 0, 8);
                                break;
                            case byte[] b:
                                buf.WriteByte(1);
                                buf.Write(BitConverter.GetBytes(b.Length), 0, 4);
                                buf.Write(b, 0, b.Length);
                                break;
                            case var v:
                                var s = Encoding.UTF8.GetBytes(Convert.ToString(v, CultureInfo.InvariantCulture));
                                buf.WriteByte(3);
                                buf.Write(BitConverter.GetBytes(s.Length), 0, 4);
                                buf.Write(s, 0, s.Length);
                                break;
                        }
                    }
                    buf.WriteByte(0x1E);
                    if (buf.Length > 1 << 20) Flush();
                }
                Flush();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return (BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant(), rows);
            }
        }
    }
}
