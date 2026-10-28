import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import org.apache.lucene.analysis.Analyzer;
import org.apache.lucene.analysis.LowerCaseFilter;
import org.apache.lucene.analysis.TokenStream;
import org.apache.lucene.analysis.miscellaneous.PerFieldAnalyzerWrapper;
import org.apache.lucene.analysis.ngram.NGramTokenFilter;
import org.apache.lucene.analysis.standard.StandardAnalyzer;
import org.apache.lucene.analysis.standard.StandardTokenizer;
import org.apache.lucene.document.Document;
import org.apache.lucene.document.Field;
import org.apache.lucene.document.IntPoint;
import org.apache.lucene.document.NumericDocValuesField;
import org.apache.lucene.document.StoredField;
import org.apache.lucene.document.StringField;
import org.apache.lucene.document.TextField;
import org.apache.lucene.index.CheckIndex;
import org.apache.lucene.index.DirectoryReader;
import org.apache.lucene.index.IndexWriter;
import org.apache.lucene.index.IndexWriterConfig;
import org.apache.lucene.index.NoMergePolicy;
import org.apache.lucene.index.SegmentInfos;
import org.apache.lucene.index.Term;
import org.apache.lucene.search.BooleanClause;
import org.apache.lucene.search.BooleanQuery;
import org.apache.lucene.search.IndexSearcher;
import org.apache.lucene.search.Query;
import org.apache.lucene.search.ScoreDoc;
import org.apache.lucene.search.Sort;
import org.apache.lucene.search.SortField;
import org.apache.lucene.search.TermQuery;
import org.apache.lucene.search.TopDocs;
import org.apache.lucene.store.Directory;
import org.apache.lucene.store.FSDirectory;
import org.apache.lucene.util.Version;

/**
 * רכיב החיפוש של עורך הספרייה: מוסיף ומוחק ספרים באינדקסי החיפוש של זית (Lucene).
 *
 * המסמכים נבנים בדיוק כמו ב-LuceneTextIndexWriter / LuceneLookupIndexWriter של זית
 * (SeforimLibrary, generator/searchindex), עם אותו ניתוח (StandardAnalyzer + 4-gram לשדה text_ng4).
 * הטקסט מגיע כבר מנורמל מעורך הספרייה (normalizeForIndexDefault).
 *
 * בטיחות: בלי מיזוג קטעים (הקובץ הגדול של האינדקס לא נכתב מחדש), שני האינדקסים נשמרים יחד
 * (prepareCommit לשניהם ואז commit), וכל כשל לפני השמירה מבטל הכול. אחרי השמירה נבדק מספר המסמכים.
 *
 * פקודות:
 *   apply  (הקלט מ-stdin ב-UTF-8, כדי שנתיבים בעברית לא ייפגעו בקידוד של Windows)
 *   count  --index DIR --lookup DIR --book ID [--book ID...]
 *   search --index DIR (--term T | --ngram T) [--book ID]
 *   check  --index DIR [--segment NAME...]
 *   segments --index DIR
 *   version
 */
public final class SefarimSearch {
    static final String TOOL_VERSION = "1.0.0";

    // שמות השדות – LuceneTextIndexWriter.Fields
    static final String FIELD_TYPE = "type";
    static final String TYPE_LINE = "line";
    static final String TYPE_BOOK_TITLE = "book_title";
    static final String FIELD_BOOK_ID = "book_id";
    static final String FIELD_CATEGORY_ID = "category_id";
    static final String FIELD_ANCESTOR_CATEGORY_IDS = "ancestor_category_ids";
    static final String FIELD_BOOK_TITLE = "book_title";
    static final String FIELD_LINE_ID = "line_id";
    static final String FIELD_LINE_INDEX = "line_index";
    static final String FIELD_TEXT = "text";
    static final String FIELD_TEXT_NG4 = "text_ng4";
    static final String FIELD_TITLE = "title";
    static final String FIELD_ORDER_INDEX = "order_index";
    static final String FIELD_IS_BASE_BOOK = "is_base_book";

    // LuceneLookupIndexWriter
    static final String TYPE_BOOK = "book";
    static final String FIELD_Q = "q";
    static final int BASE_BOOK_TERM_MULTIPLIER = 120;

    public static void main(String[] args) {
        System.setOut(new PrintStream(System.out, true, StandardCharsets.UTF_8));
        System.setErr(new PrintStream(System.err, true, StandardCharsets.UTF_8));
        try {
            if (args.length == 0) throw new IllegalArgumentException("missing command");
            Map<String, List<String>> a = parseArgs(args);
            switch (args[0]) {
                case "apply" -> apply(new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8)));
                case "count" -> count(path(a, "--index"), path(a, "--lookup"), longs(a, "--book"));
                case "search" -> search(path(a, "--index"), one(a, "--term"), one(a, "--ngram"), longs(a, "--book"));
                case "check" -> check(path(a, "--index"), a.getOrDefault("--segment", List.of()));
                case "probe" -> probe(new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8)));
                case "segments" -> segments(path(a, "--index"));
                case "version" -> System.out.println("sefarim-search " + TOOL_VERSION + " lucene " + Version.LATEST);
                default -> throw new IllegalArgumentException("unknown command: " + args[0]);
            }
            System.exit(0);
        } catch (Throwable t) {
            System.err.println("ERROR " + t);
            t.printStackTrace(System.err);
            System.exit(2);
        }
    }

    // ───────────────────────── ניתוח, כמו BuildLuceneIndex.kt ─────────────────────────

    static Analyzer analyzer() {
        Analyzer ngram4 = new Analyzer() {
            @Override
            protected TokenStreamComponents createComponents(String fieldName) {
                StandardTokenizer src = new StandardTokenizer();
                TokenStream ts = new LowerCaseFilter(src);
                ts = new NGramTokenFilter(ts, 4, 4, false);
                return new TokenStreamComponents(src, ts);
            }
        };
        return new PerFieldAnalyzerWrapper(new StandardAnalyzer(), Map.of(FIELD_TEXT_NG4, ngram4));
    }

    static IndexWriterConfig config(boolean lookup) {
        IndexWriterConfig cfg = new IndexWriterConfig(analyzer());
        cfg.setOpenMode(IndexWriterConfig.OpenMode.APPEND);
        // בלי מיזוג: הקטע הגדול (4.7GB) לעולם לא נכתב מחדש; כל פעולה מוסיפה קטע קטן.
        cfg.setMergePolicy(NoMergePolicy.INSTANCE);
        cfg.setCommitOnClose(false);
        if (lookup) {
            // אינדקס השמות ממוין כך (LuceneLookupIndexWriter); Lucene דורש את אותו מיון בפתיחה.
            cfg.setIndexSort(new Sort(
                    new SortField(FIELD_IS_BASE_BOOK, SortField.Type.INT, true),
                    new SortField(FIELD_ORDER_INDEX, SortField.Type.INT)));
        }
        return cfg;
    }

    // ───────────────────────── apply ─────────────────────────

    static final class Book {
        long id;
        long categoryId;
        boolean isBaseBook;
        int orderIndex;
        String title;
        List<String> titleTerms = new ArrayList<>();
        List<String> lookupTerms = new ArrayList<>();
        List<Long> ancestors = new ArrayList<>();
        int lines;
        boolean docsAdded;
    }

    /**
     * הקלט (UTF-8, שורה לכל רשומה, שדות מופרדים בטאב). שתי הרשומות הראשונות הן הנתיבים:
     *   INDEX  path                                  – אינדקס הטקסט (seforim.db.lucene)
     *   LOOKUP path                                  – אינדקס השמות (seforim.db.lookup.lucene)
     *   DEL  bookId                                  – מחיקת כל המסמכים של הספר משני האינדקסים
     *   BOOK bookId categoryId isBase orderIndex title – תחילת ספר להוספה (קודם נמחק כל מה שיש לו)
     *   ANC  id,id,...                               – קטגוריות האב (כולל הקטגוריה עצמה)
     *   TERM term                                    – מסמך book_title באינדקס הטקסט
     *   LOOK term                                    – מונח באינדקס השמות
     *   LINE lineId lineIndex normalizedText         – שורה
     *   END  lineCount                               – סוף ספר (מספר השורות לבדיקה)
     */
    static void apply(BufferedReader r) throws IOException {
        Path index = header(r, "INDEX");
        Path lookup = header(r, "LOOKUP");
        if (!Files.isDirectory(index) || !Files.isDirectory(lookup)) throw new IOException("index folder not found");
        List<Long> deletes = new ArrayList<>();
        Map<Long, Book> books = new LinkedHashMap<>();
        try (Directory td = FSDirectory.open(index);
             Directory ld = FSDirectory.open(lookup);
             IndexWriter tw = new IndexWriter(td, config(false));
             IndexWriter lw = new IndexWriter(ld, config(true))) {
            try {
                Book cur = null;
                String line;
                int n = 2;
                while ((line = r.readLine()) != null) {
                    n++;
                    if (line.isEmpty()) continue;
                    String[] f = line.split("\t", -1);
                    switch (f[0]) {
                        case "DEL" -> {
                            long id = Long.parseLong(f[1]);
                            deleteBook(tw, lw, id);
                            deletes.add(id);
                        }
                        case "BOOK" -> {
                            if (cur != null) throw new IOException("BOOK before END at line " + n);
                            cur = new Book();
                            cur.id = Long.parseLong(f[1]);
                            cur.categoryId = Long.parseLong(f[2]);
                            cur.isBaseBook = "1".equals(f[3]);
                            cur.orderIndex = Integer.parseInt(f[4]);
                            cur.title = f[5];
                            deleteBook(tw, lw, cur.id);
                        }
                        case "ANC" -> {
                            req(cur, n);
                            for (String s : f[1].split(",")) if (!s.isEmpty()) cur.ancestors.add(Long.parseLong(s));
                        }
                        case "TERM" -> req(cur, n).titleTerms.add(f[1]);
                        case "LOOK" -> req(cur, n).lookupTerms.add(f[1]);
                        case "LINE" -> {
                            req(cur, n);
                            addBookDocs(tw, lw, cur);
                            tw.addDocument(lineDoc(cur, Long.parseLong(f[1]), Integer.parseInt(f[2]), f[3]));
                            cur.lines++;
                        }
                        case "END" -> {
                            req(cur, n);
                            addBookDocs(tw, lw, cur);
                            if (Integer.parseInt(f[1]) != cur.lines)
                                throw new IOException("line count mismatch for book " + cur.id + ": " + cur.lines + " != " + f[1]);
                            books.put(cur.id, cur);
                            cur = null;
                        }
                        default -> throw new IOException("unknown record at line " + n + ": " + f[0]);
                    }
                }
                if (cur != null) throw new IOException("missing END");

                // שמירה בשני שלבים: אם אחד מהאינדקסים נכשל בהכנה, שניהם מתבטלים.
                tw.prepareCommit();
                lw.prepareCommit();
            } catch (Throwable t) {
                tw.rollback();
                lw.rollback();
                throw t;
            }
            tw.commit();
            lw.commit();
        }
        verify(index, lookup, books, deletes);
        System.out.println("OK added=" + books.size() + " deleted=" + deletes.size());
    }

    static Path header(BufferedReader r, String name) throws IOException {
        String line = r.readLine();
        if (line == null || !line.startsWith(name + "\t")) throw new IOException("expected " + name);
        return Paths.get(line.substring(name.length() + 1));
    }

    static Book req(Book cur, int n) throws IOException {
        if (cur == null) throw new IOException("record outside BOOK at line " + n);
        return cur;
    }

    static void deleteBook(IndexWriter tw, IndexWriter lw, long id) throws IOException {
        tw.deleteDocuments(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(id)));
        lw.deleteDocuments(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(id)));
    }

    /** מסמכי השם של הספר (באינדקס הטקסט ובאינדקס השמות) – פעם אחת לכל ספר, לפני השורה הראשונה. */
    static void addBookDocs(IndexWriter tw, IndexWriter lw, Book b) throws IOException {
        if (b.docsAdded) return;
        for (String term : b.titleTerms) tw.addDocument(titleDoc(b, term));
        lw.addDocument(lookupDoc(b));
        b.docsAdded = true;
    }

    // LuceneTextIndexWriter.addLine
    static Document lineDoc(Book b, long lineId, int lineIndex, String normalizedText) {
        Document doc = new Document();
        doc.add(new StringField(FIELD_TYPE, TYPE_LINE, Field.Store.NO));
        doc.add(new StoredField(FIELD_BOOK_ID, b.id));
        doc.add(new IntPoint(FIELD_BOOK_ID, Math.toIntExact(b.id)));
        doc.add(new NumericDocValuesField(FIELD_BOOK_ID, b.id));
        doc.add(new StoredField(FIELD_CATEGORY_ID, b.categoryId));
        doc.add(new IntPoint(FIELD_CATEGORY_ID, Math.toIntExact(b.categoryId)));
        doc.add(new StoredField(FIELD_BOOK_TITLE, b.title));
        StringBuilder anc = new StringBuilder();
        for (long id : b.ancestors) {
            doc.add(new IntPoint(FIELD_ANCESTOR_CATEGORY_IDS, Math.toIntExact(id)));
            if (anc.length() > 0) anc.append(',');
            anc.append(id);
        }
        doc.add(new StoredField(FIELD_ANCESTOR_CATEGORY_IDS, anc.toString()));
        doc.add(new StoredField(FIELD_LINE_ID, lineId));
        doc.add(new IntPoint(FIELD_LINE_ID, Math.toIntExact(lineId)));
        doc.add(new StoredField(FIELD_LINE_INDEX, lineIndex));
        doc.add(new IntPoint(FIELD_LINE_INDEX, lineIndex));
        int order = boostedOrder(b);
        doc.add(new StoredField(FIELD_ORDER_INDEX, order));
        doc.add(new IntPoint(FIELD_ORDER_INDEX, order));
        doc.add(new StoredField(FIELD_IS_BASE_BOOK, b.isBaseBook ? 1 : 0));
        doc.add(new IntPoint(FIELD_IS_BASE_BOOK, b.isBaseBook ? 1 : 0));
        doc.add(new TextField(FIELD_TEXT, normalizedText, Field.Store.NO));
        doc.add(new TextField(FIELD_TEXT_NG4, normalizedText, Field.Store.NO));
        return doc;
    }

    // BuildLuceneIndex: ספרי בסיס מקבלים סדר מוקדם יותר בחמישה
    static int boostedOrder(Book b) {
        return b.isBaseBook ? Math.max(b.orderIndex - 5, 1) : b.orderIndex;
    }

    // LuceneTextIndexWriter.addBookTitleTerm
    static Document titleDoc(Book b, String term) {
        Document doc = new Document();
        doc.add(new StringField(FIELD_TYPE, TYPE_BOOK_TITLE, Field.Store.NO));
        doc.add(new StoredField(FIELD_BOOK_ID, b.id));
        doc.add(new IntPoint(FIELD_BOOK_ID, Math.toIntExact(b.id)));
        doc.add(new NumericDocValuesField(FIELD_BOOK_ID, b.id));
        doc.add(new StoredField(FIELD_CATEGORY_ID, b.categoryId));
        doc.add(new IntPoint(FIELD_CATEGORY_ID, Math.toIntExact(b.categoryId)));
        doc.add(new StoredField(FIELD_BOOK_TITLE, b.title));
        doc.add(new TextField(FIELD_TITLE, term, Field.Store.NO));
        return doc;
    }

    // LuceneLookupIndexWriter.addBook
    static Document lookupDoc(Book b) {
        int repeats = b.isBaseBook ? BASE_BOOK_TERM_MULTIPLIER : 1;
        int order = b.orderIndex;
        Document doc = new Document();
        doc.add(new StringField(FIELD_TYPE, TYPE_BOOK, Field.Store.NO));
        doc.add(new StoredField(FIELD_BOOK_ID, b.id));
        doc.add(new IntPoint(FIELD_BOOK_ID, Math.toIntExact(b.id)));
        doc.add(new NumericDocValuesField(FIELD_BOOK_ID, b.id));
        doc.add(new StoredField(FIELD_CATEGORY_ID, b.categoryId));
        doc.add(new IntPoint(FIELD_CATEGORY_ID, Math.toIntExact(b.categoryId)));
        doc.add(new StoredField(FIELD_BOOK_TITLE, b.title));
        doc.add(new StoredField(FIELD_IS_BASE_BOOK, b.isBaseBook ? 1 : 0));
        doc.add(new IntPoint(FIELD_IS_BASE_BOOK, b.isBaseBook ? 1 : 0));
        doc.add(new NumericDocValuesField(FIELD_IS_BASE_BOOK, b.isBaseBook ? 1L : 0L));
        doc.add(new StoredField(FIELD_ORDER_INDEX, order));
        doc.add(new IntPoint(FIELD_ORDER_INDEX, order));
        doc.add(new NumericDocValuesField(FIELD_ORDER_INDEX, order));
        for (int i = 0; i < repeats; i++)
            for (String t : b.lookupTerms) doc.add(new TextField(FIELD_Q, t, Field.Store.NO));
        return doc;
    }

    // ───────────────────────── בדיקה אחרי השמירה ─────────────────────────

    static void verify(Path index, Path lookup, Map<Long, Book> books, List<Long> deletes) throws IOException {
        try (Directory td = FSDirectory.open(index); Directory ld = FSDirectory.open(lookup);
             DirectoryReader tr = DirectoryReader.open(td); DirectoryReader lr = DirectoryReader.open(ld)) {
            IndexSearcher ts = new IndexSearcher(tr);
            IndexSearcher ls = new IndexSearcher(lr);
            for (Book b : books.values()) {
                int lines = ts.count(byBookAndType(b.id, TYPE_LINE));
                int titles = ts.count(byBookAndType(b.id, TYPE_BOOK_TITLE));
                int looks = ls.count(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(b.id)));
                if (lines != b.lines || titles != b.titleTerms.size() || looks != 1)
                    throw new IOException("verification failed for book " + b.id + ": lines " + lines + "/" + b.lines +
                            ", titles " + titles + "/" + b.titleTerms.size() + ", lookup " + looks + "/1");
            }
            for (long id : deletes) {
                if (books.containsKey(id)) continue;
                int left = ts.count(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(id))) +
                           ls.count(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(id)));
                if (left != 0) throw new IOException("verification failed: book " + id + " still has " + left + " documents");
            }
        }
    }

    static Query byBookAndType(long bookId, String type) {
        return new BooleanQuery.Builder()
                .add(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(bookId)), BooleanClause.Occur.FILTER)
                .add(new TermQuery(new Term(FIELD_TYPE, type)), BooleanClause.Occur.FILTER)
                .build();
    }

    // ───────────────────────── פקודות בדיקה (קריאה בלבד) ─────────────────────────

    static void count(Path index, Path lookup, List<Long> ids) throws IOException {
        try (Directory td = FSDirectory.open(index); Directory ld = FSDirectory.open(lookup);
             DirectoryReader tr = DirectoryReader.open(td); DirectoryReader lr = DirectoryReader.open(ld)) {
            IndexSearcher ts = new IndexSearcher(tr);
            IndexSearcher ls = new IndexSearcher(lr);
            System.out.println("index docs=" + tr.numDocs() + " maxDoc=" + tr.maxDoc() + " segments=" + tr.leaves().size() +
                    "; lookup docs=" + lr.numDocs() + " segments=" + lr.leaves().size());
            for (long id : ids) {
                System.out.println("book " + id + " lines=" + ts.count(byBookAndType(id, TYPE_LINE)) +
                        " titles=" + ts.count(byBookAndType(id, TYPE_BOOK_TITLE)) +
                        " lookup=" + ls.count(IntPoint.newExactQuery(FIELD_BOOK_ID, Math.toIntExact(id))));
            }
        }
    }

    static void search(Path index, String term, String ngram, List<Long> books) throws IOException {
        try (Directory td = FSDirectory.open(index); DirectoryReader tr = DirectoryReader.open(td)) {
            IndexSearcher ts = new IndexSearcher(tr);
            BooleanQuery.Builder q = new BooleanQuery.Builder();
            q.add(new TermQuery(new Term(FIELD_TYPE, TYPE_LINE)), BooleanClause.Occur.FILTER);
            if (term != null) q.add(new TermQuery(new Term(FIELD_TEXT, term)), BooleanClause.Occur.MUST);
            if (ngram != null) q.add(new TermQuery(new Term(FIELD_TEXT_NG4, ngram)), BooleanClause.Occur.MUST);
            if (!books.isEmpty()) q.add(IntPoint.newSetQuery(FIELD_BOOK_ID, books.stream().mapToInt(Math::toIntExact).toArray()), BooleanClause.Occur.FILTER);
            TopDocs top = ts.search(q.build(), 20);
            System.out.println("hits=" + top.totalHits);
            var stored = ts.storedFields();
            for (ScoreDoc sd : top.scoreDocs) {
                Document d = stored.document(sd.doc);
                System.out.println("book=" + d.get(FIELD_BOOK_ID) + " line=" + d.get(FIELD_LINE_ID) + " index=" + d.get(FIELD_LINE_INDEX) +
                        " title=" + d.get(FIELD_BOOK_TITLE) + " anc=" + d.get(FIELD_ANCESTOR_CATEGORY_IDS));
            }
        }
    }

    static void check(Path index, List<String> segments) throws IOException {
        try (Directory d = FSDirectory.open(index); CheckIndex ci = new CheckIndex(d)) {
            ci.setInfoStream(System.out, false);
            CheckIndex.Status st = ci.checkIndex(segments.isEmpty() ? null : segments);
            System.out.println(st.clean ? "CLEAN" : "BROKEN");
            if (!st.clean) throw new IOException("index is not clean");
        }
    }

    /**
     * בדיקת הנרמול מול האינדקס הרשמי (קריאה בלבד): לכל שורה רשמית (INDEX ואז LINE lineId lineIndex text),
     * כל מילה שהניתוח מפיק מהטקסט שלנו – בשדה text ובשדה text_ng4 – חייבת להופיע באינדקס באותו מסמך בדיוק,
     * ומספר המילים חייב להיות זהה (לפי norms לא אפשרי; לכן בודקים גם שאין מסמך נוסף לאותה שורה).
     */
    static void probe(BufferedReader r) throws IOException {
        Path index = header(r, "INDEX");
        Analyzer an = analyzer();
        int lines = 0, terms = 0, missing = 0;
        try (Directory td = FSDirectory.open(index); DirectoryReader tr = DirectoryReader.open(td)) {
            IndexSearcher ts = new IndexSearcher(tr);
            String line;
            while ((line = r.readLine()) != null) {
                String[] f = line.split("\t", -1);
                if (!"LINE".equals(f[0])) continue;
                int lineId = Integer.parseInt(f[1]);
                Query byLine = IntPoint.newExactQuery(FIELD_LINE_ID, lineId);
                if (ts.count(byLine) != 1) {
                    System.out.println("NODOC " + lineId);
                    missing++;
                    continue;
                }
                lines++;
                int docId = ts.search(byLine, 1).scoreDocs[0].doc;
                var leaves = tr.leaves();
                var leaf = leaves.get(org.apache.lucene.index.ReaderUtil.subIndex(docId, leaves));
                for (String field : new String[] { FIELD_TEXT, FIELD_TEXT_NG4 }) {
                    java.util.Set<String> toks = new java.util.LinkedHashSet<>();
                    int length = 0, overlap = 0;
                    try (TokenStream ts2 = an.tokenStream(field, f[3])) {
                        var att = ts2.addAttribute(org.apache.lucene.analysis.tokenattributes.CharTermAttribute.class);
                        var pos = ts2.addAttribute(org.apache.lucene.analysis.tokenattributes.PositionIncrementAttribute.class);
                        ts2.reset();
                        while (ts2.incrementToken()) {
                            toks.add(att.toString());
                            length++;
                            if (pos.getPositionIncrement() == 0) overlap++;
                        }
                        ts2.end();
                    }
                    // הכיוון ההפוך: אורך השדה השמור באינדקס (norm של BM25) חייב להתאים למספר המילים שלנו.
                    var norms = leaf.reader().getNormValues(field);
                    if (norms != null && norms.advanceExact(docId - leaf.docBase)) {
                        long expected = org.apache.lucene.util.SmallFloat.intToByte4(length - overlap);
                        if (norms.longValue() != expected) {
                            missing++;
                            if (missing <= 30) System.out.println("NORM line=" + lineId + " field=" + field + " index=" + norms.longValue() + " ours=" + expected);
                        }
                    } else if (length > 0) {
                        missing++;
                        if (missing <= 30) System.out.println("NONORM line=" + lineId + " field=" + field);
                    }
                    for (String tok : toks) {
                        terms++;
                        Query q = new BooleanQuery.Builder()
                                .add(byLine, BooleanClause.Occur.FILTER)
                                .add(new TermQuery(new Term(field, tok)), BooleanClause.Occur.FILTER)
                                .build();
                        if (ts.count(q) != 1) {
                            missing++;
                            if (missing <= 30) System.out.println("MISS line=" + lineId + " field=" + field + " term=" + tok);
                        }
                    }
                }
            }
        }
        System.out.println("PROBE lines=" + lines + " terms=" + terms + " missing=" + missing);
        if (missing > 0) throw new IOException("normalization differs from the official index");
    }

    static void segments(Path index) throws IOException {
        try (Directory d = FSDirectory.open(index)) {
            SegmentInfos infos = SegmentInfos.readLatestCommit(d);
            System.out.println("generation=" + infos.getGeneration());
            for (var si : infos) System.out.println(si.info.name + " docs=" + si.info.maxDoc() + " del=" + si.getDelCount());
        }
    }

    // ───────────────────────── עזר ─────────────────────────

    static Map<String, List<String>> parseArgs(String[] args) {
        Map<String, List<String>> m = new HashMap<>();
        for (int i = 1; i < args.length; i++) {
            if (!args[i].startsWith("--")) throw new IllegalArgumentException("unexpected argument: " + args[i]);
            if (i + 1 >= args.length) throw new IllegalArgumentException("missing value for " + args[i]);
            m.computeIfAbsent(args[i], k -> new ArrayList<>()).add(args[++i]);
        }
        return m;
    }

    static Path path(Map<String, List<String>> a, String k) {
        String v = one(a, k);
        if (v == null) throw new IllegalArgumentException("missing " + k);
        return Paths.get(v);
    }

    static String one(Map<String, List<String>> a, String k) {
        List<String> v = a.get(k);
        return v == null || v.isEmpty() ? null : v.get(0);
    }

    static List<Long> longs(Map<String, List<String>> a, String k) {
        List<Long> r = new ArrayList<>();
        for (String s : a.getOrDefault(k, List.of())) r.add(Long.parseLong(s));
        return r;
    }
}
