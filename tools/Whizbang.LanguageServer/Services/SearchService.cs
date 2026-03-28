using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Whizbang.LanguageServer.Protocol;

namespace Whizbang.LanguageServer.Services;

public sealed record SearchDocument {
  public required string Slug { get; init; }
  public required string Title { get; init; }
  public required string Category { get; init; }
  public required string Content { get; init; }
  public required string Preview { get; init; }
}

public sealed class SearchService : IDisposable {
  private const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;

  private RAMDirectory? _directory;
  private IndexSearcher? _searcher;
  private Dictionary<string, List<string>> _synonymMap = new(StringComparer.OrdinalIgnoreCase);

  // Reverse map: synonym value -> canonical key
  private Dictionary<string, string> _reverseSynonymMap = new(StringComparer.OrdinalIgnoreCase);

  public void BuildIndex(IReadOnlyList<SearchDocument> documents) {
    _directory?.Dispose();
    _directory = new RAMDirectory();

    using var analyzer = new StandardAnalyzer(AppLuceneVersion);
    var config = new IndexWriterConfig(AppLuceneVersion, analyzer);

    using (var writer = new IndexWriter(_directory, config)) {
      foreach (var doc in documents) {
        var luceneDoc = new Document();

        // slug: stored, not indexed
        luceneDoc.Add(new StoredField("slug", doc.Slug));

        // title: stored, indexed, boosted 3x
        var titleField = new TextField("title", doc.Title, Field.Store.YES) {
          Boost = 3.0f
        };
        luceneDoc.Add(titleField);

        // category: stored, indexed, boosted 2x
        var categoryField = new TextField("category", doc.Category, Field.Store.YES) {
          Boost = 2.0f
        };
        luceneDoc.Add(categoryField);

        // content: not stored, indexed
        luceneDoc.Add(new TextField("content", doc.Content, Field.Store.NO));

        // preview: stored, not indexed
        luceneDoc.Add(new StoredField("preview", doc.Preview));

        writer.AddDocument(luceneDoc);
      }

      writer.Commit();
    }

    var reader = DirectoryReader.Open(_directory);
    _searcher = new IndexSearcher(reader);
  }

  public void SetSynonyms(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms) {
    _synonymMap.Clear();
    _reverseSynonymMap.Clear();

    foreach (var (key, values) in synonyms) {
      _synonymMap[key] = values.ToList();
      foreach (var value in values) {
        _reverseSynonymMap[value] = key;
      }
    }
  }

  public IReadOnlyList<SearchResult> Search(string query, int maxResults = 20) {
    if (string.IsNullOrWhiteSpace(query) || _searcher is null) {
      return [];
    }

    var expandedQuery = ExpandQuery(query.Trim());

    using var analyzer = new StandardAnalyzer(AppLuceneVersion);
    var parser = new MultiFieldQueryParser(
      AppLuceneVersion,
      ["title", "category", "content"],
      analyzer);
    parser.DefaultOperator = Operator.OR;

    Query parsedQuery;
    try {
      parsedQuery = parser.Parse(expandedQuery);
    } catch (ParseException) {
      // If expanded query fails to parse, try the original
      try {
        parsedQuery = parser.Parse(QueryParserBase.Escape(query.Trim()));
      } catch (ParseException) {
        return [];
      }
    }

    // Also add fuzzy and prefix queries for each term
    var booleanQuery = new BooleanQuery {
      { parsedQuery, Occur.SHOULD }
    };

    var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    foreach (var term in terms) {
      var lowerTerm = term.ToLowerInvariant();

      // Fuzzy match
      foreach (var field in new[] { "title", "category", "content" }) {
        var fuzzyQuery = new FuzzyQuery(new Term(field, lowerTerm), 1);
        booleanQuery.Add(fuzzyQuery, Occur.SHOULD);
      }

      // Prefix match
      foreach (var field in new[] { "title", "category", "content" }) {
        var prefixQuery = new PrefixQuery(new Term(field, lowerTerm));
        prefixQuery.Boost = 0.5f;
        booleanQuery.Add(prefixQuery, Occur.SHOULD);
      }
    }

    // Fetch more than maxResults to account for deduplication
    var topDocs = _searcher.Search(booleanQuery, maxResults * 3);

    // Deduplicate by slug, keeping highest score
    var seenSlugs = new Dictionary<string, SearchResult>(StringComparer.Ordinal);

    foreach (var scoreDoc in topDocs.ScoreDocs) {
      var doc = _searcher.Doc(scoreDoc.Doc);
      var slug = doc.Get("slug");

      if (seenSlugs.ContainsKey(slug)) {
        continue; // Already have a higher-scoring result for this slug
      }

      seenSlugs[slug] = new SearchResult {
        Title = doc.Get("title") ?? "",
        Category = doc.Get("category") ?? "",
        Slug = slug,
        Preview = doc.Get("preview") ?? "",
        Score = Math.Round(scoreDoc.Score, 4)
      };

      if (seenSlugs.Count >= maxResults) {
        break;
      }
    }

    return seenSlugs.Values.ToList();
  }

  private string ExpandQuery(string query) {
    var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var expandedParts = new List<string>();

    // Check if the full query matches a synonym value
    if (_reverseSynonymMap.TryGetValue(query, out var canonicalKey)) {
      expandedParts.Add(canonicalKey);
      if (_synonymMap.TryGetValue(canonicalKey, out var relatedSynonyms)) {
        foreach (var syn in relatedSynonyms) {
          // Add multi-word synonyms as quoted phrases
          expandedParts.Add(syn.Contains(' ') ? $"\"{syn}\"" : syn);
        }
      }

      return string.Join(" OR ", expandedParts);
    }

    // Check each individual term
    foreach (var term in terms) {
      expandedParts.Add(term);

      // Check if term is a synonym key
      if (_synonymMap.TryGetValue(term, out var synonyms)) {
        foreach (var syn in synonyms) {
          expandedParts.Add(syn.Contains(' ') ? $"\"{syn}\"" : syn);
        }
      }

      // Check if term is a synonym value
      if (_reverseSynonymMap.TryGetValue(term, out var key)) {
        expandedParts.Add(key);
        if (_synonymMap.TryGetValue(key, out var related)) {
          foreach (var syn in related) {
            if (!string.Equals(syn, term, StringComparison.OrdinalIgnoreCase)) {
              expandedParts.Add(syn.Contains(' ') ? $"\"{syn}\"" : syn);
            }
          }
        }
      }
    }

    return string.Join(" OR ", expandedParts.Distinct(StringComparer.OrdinalIgnoreCase));
  }

  public void Dispose() {
    if (_searcher?.IndexReader is not null) {
      _searcher.IndexReader.Dispose();
    }

    _directory?.Dispose();
    _directory = null;
    _searcher = null;
  }
}
