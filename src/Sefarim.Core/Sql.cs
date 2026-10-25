using System.Collections.Generic;
using System.Linq;

namespace Sefarim.Core
{
    internal static class Sql
    {
        /// <summary>מצטט שם טבלה או עמודה.</summary>
        public static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

        public static string List(IEnumerable<string> idents, string prefix = null) =>
            string.Join(",", idents.Select(i => (prefix == null ? "" : prefix + ".") + Q(i)));

        /// <summary>תנאי שוויון בין שני כינויים על אותן עמודות: a.x=b.x AND a.y=b.y</summary>
        public static string JoinOn(IEnumerable<string> cols, string left, string right) =>
            string.Join(" AND ", cols.Select(c => left + "." + Q(c) + " IS " + right + "." + Q(c)));
    }
}
