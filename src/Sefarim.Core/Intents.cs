using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sefarim.Core
{
    /// <summary>מה המשתמש ביקש לעשות. נשמר כדי שאפשר יהיה לחזור על הפעולה אחרי עדכון ספרייה.</summary>
    public sealed class OpIntent
    {
        public const string DeleteBooks = "deleteBooks";
        public const string DeleteCategories = "deleteCategories";
        public const string EditBook = "editBook";
        public const string EditCategory = "editCategory";

        public string Kind { get; set; }
        public string Summary { get; set; }
        public List<BookRef> Books { get; set; } = new List<BookRef>();
        public List<CategoryRef> Categories { get; set; } = new List<CategoryRef>();
        public BookChanges BookChanges { get; set; }
        public CategoryChanges CategoryChanges { get; set; }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public static OpIntent FromJson(string json) => JsonSerializer.Deserialize<OpIntent>(json, Options);
    }

    public sealed class BookRef
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string CategoryPath { get; set; }

        public static BookRef Of(BookInfo b) => new BookRef
        {
            Id = b.Id,
            Title = b.Title,
            CategoryPath = b.Category?.Path,
        };
    }

    public sealed class CategoryRef
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Path { get; set; }

        public static CategoryRef Of(CategoryNode c) => new CategoryRef { Id = c.Id, Title = c.Title, Path = c.Path };
    }

    /// <summary>שדות שהשתנו בספר. null = לא השתנה.</summary>
    public sealed class BookChanges
    {
        public string Title { get; set; }
        public string ShortDesc { get; set; }
        public string LongDesc { get; set; }
        public int? OrderIndex { get; set; }
        public CategoryRef MoveTo { get; set; }
        public List<string> Authors { get; set; }

        public bool IsEmpty => Title == null && ShortDesc == null && LongDesc == null && OrderIndex == null &&
                               MoveTo == null && Authors == null;
    }

    public sealed class CategoryChanges
    {
        public string Title { get; set; }
        public int? OrderIndex { get; set; }

        public bool IsEmpty => Title == null && OrderIndex == null;
    }

    public enum OpState
    {
        Pending,
        Applied,
        Restored,

        /// <summary>
        /// מוחלת בקובץ, אבל בלי שורות בגיבוי (מצב "עדכון עצמי"): אי אפשר להחזיר אותה,
        /// רק להחיל אותה שוב אחרי עדכון. נשמר רק מה המשתמש ביקש.
        /// </summary>
        Permanent,
    }

    /// <summary>פעולה שבוצעה בקובץ, כפי שהיא רשומה במאגר הגיבוי.</summary>
    public sealed class AppliedOp
    {
        public long Id { get; set; }
        public OpState State { get; set; }
        public OpIntent Intent { get; set; }
        public long? DbVersion { get; set; }
        public DateTime Created { get; set; }
        public long Rows { get; set; }
        public List<string> Tables { get; set; } = new List<string>();
        public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();

        /// <summary>טביעת אצבע של אותן שורות מיד אחרי השינוי, כדי לזהות אם השינוי עדיין בקובץ.</summary>
        public Dictionary<string, string> AfterHashes { get; set; } = new Dictionary<string, string>();
    }
}
