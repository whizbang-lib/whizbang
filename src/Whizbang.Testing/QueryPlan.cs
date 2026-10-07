// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Whizbang.Testing;

/// <summary>
/// A captured <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> result, with assertions that state what a query's
/// plan must look like — no sequential scan of a named table, an index actually read, a ceiling on the pages
/// visited.
/// </summary>
/// <remarks>
/// <para>
/// A correctness test cannot see a plan regression: the rows returned are identical whether the planner used the
/// index or scanned the table. Matching on <c>EXPLAIN</c> text can prove an index is reachable, but it cannot say
/// how much work the query did, and at test-sized data the text of a good plan and a catastrophic one look alike.
/// What separates them is the pages visited against the rows returned.
/// </para>
/// <para>
/// So the rules here are about work, not shape alone. <see cref="MustNotAmplifyBeyond"/> is the one that catches a
/// scan nested inside a per-row loop: a measured work claim returned 13 correct rows after visiting 79,590,235
/// shared buffers, roughly 600 GiB of buffer traffic, which reads as contention if only its duration is known.
/// </para>
/// <para>
/// Parsing is separate from capture so that every rule is a pure function of the plan's JSON and can be tested
/// without a database. <see cref="CaptureAsync"/> is the only member that needs a connection.
/// </para>
/// </remarks>
/// <docs>fundamentals/testing/query-plans</docs>
/// <tests>tests/Whizbang.Testing.Tests/QueryPlanTests.cs</tests>
public sealed class QueryPlan {
  private readonly List<QueryPlanNode> _nodes;

  private QueryPlan(string json, List<QueryPlanNode> nodes, QueryPlanNode root) {
    Json = json;
    _nodes = nodes;
    SharedBuffersHit = root.SharedHit;
    SharedBuffersRead = root.SharedRead;
    ActualRows = root.ActualRows;
    ActualTotalTimeMs = root.ActualTotalTimeMs;
  }

  /// <summary>The raw <c>EXPLAIN</c> JSON, so a failure can quote the plan the planner actually chose.</summary>
  public string Json { get; }

  /// <summary>Every node of the plan tree, flattened depth-first. The root is first.</summary>
  public IReadOnlyList<QueryPlanNode> Nodes => _nodes;

  /// <summary>Shared buffers the whole query found in cache. Zero when captured without <c>BUFFERS</c>.</summary>
  public long SharedBuffersHit { get; }

  /// <summary>Shared buffers the whole query read from disk. Zero when captured without <c>BUFFERS</c>.</summary>
  public long SharedBuffersRead { get; }

  /// <summary>Rows the query returned.</summary>
  public long ActualRows { get; }

  /// <summary>What the query took, in milliseconds, as <c>ANALYZE</c> measured it.</summary>
  public double ActualTotalTimeMs { get; }

  /// <summary>Every shared buffer the query visited, cached or not.</summary>
  public long SharedBuffersVisited => SharedBuffersHit + SharedBuffersRead;

  /// <summary>
  /// Runs <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> for <paramref name="sql"/> and captures the plan.
  /// </summary>
  /// <remarks>
  /// <c>ANALYZE</c> executes the statement, so a caller explaining a mutation should do it inside a transaction it
  /// rolls back. Parameters are bound by name, exactly as the statement under test would bind them: a plan taken
  /// with inlined literals is a different plan from the one production gets.
  /// </remarks>
  public static async Task<QueryPlan> CaptureAsync(
      NpgsqlConnection connection,
      string sql,
      IReadOnlyDictionary<string, object?>? parameters = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(sql);

    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // EXPLAIN takes a STATEMENT, and no parameter can carry one, so there is no
    // parameterized form of this to prefer. The concatenated text is the statement the test author is explaining
    // -- test source, never request input -- and its own values still bind through `parameters` below, which is
    // the point: a plan taken with inlined literals is a different plan from the one production gets.
    command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql;
#pragma warning restore S2077
    if (parameters is not null) {
      foreach (var (name, value) in parameters) {
        command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
      }
    }

    var json = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string;
    return Parse(json ?? string.Empty);
  }

  /// <summary>Parses an <c>EXPLAIN … FORMAT JSON</c> result.</summary>
  /// <exception cref="ArgumentException">
  /// The text is not an <c>EXPLAIN FORMAT JSON</c> result. Returning an empty plan instead would make every
  /// structural rule pass vacuously, which is the one outcome a plan test must never have.
  /// </exception>
  public static QueryPlan Parse(string explainJson) {
    if (string.IsNullOrWhiteSpace(explainJson)) {
      throw new ArgumentException(
        "No EXPLAIN output to parse. Capture it with EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON).", nameof(explainJson));
    }

    JsonElement root;
    try {
      using var document = JsonDocument.Parse(explainJson);
      root = document.RootElement.Clone();
    } catch (JsonException error) {
      throw new ArgumentException(
        "EXPLAIN output is not valid JSON. Use FORMAT JSON, not the default text format.", nameof(explainJson), error);
    }

    // EXPLAIN FORMAT JSON yields an array with one entry per statement; the plan hangs off "Plan".
    if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
        || !root[0].TryGetProperty("Plan", out var planElement)) {
      throw new ArgumentException(
        "EXPLAIN output carries no plan. Expected an array whose first entry has a \"Plan\" property.",
        nameof(explainJson));
    }

    var nodes = new List<QueryPlanNode>();
    _flatten(planElement, nodes);
    return new QueryPlan(explainJson, nodes, nodes[0]);
  }

  private static void _flatten(JsonElement element, List<QueryPlanNode> into) {
    into.Add(new QueryPlanNode(
      nodeType: _text(element, "Node Type") ?? "(unknown)",
      relationName: _text(element, "Relation Name"),
      indexName: _text(element, "Index Name"),
      functionName: _text(element, "Function Name"),
      actualRows: _number(element, "Actual Rows"),
      actualLoops: _number(element, "Actual Loops"),
      actualTotalTimeMs: _double(element, "Actual Total Time"),
      sharedHit: _number(element, "Shared Hit Blocks"),
      sharedRead: _number(element, "Shared Read Blocks")));

    if (element.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, into);
      }
    }
  }

  private static string? _text(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  private static long _number(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0L;

  private static double _double(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0d;

  /// <summary>
  /// Asserts that no node of the plan sequentially scans any of <paramref name="relations"/>.
  /// </summary>
  /// <remarks>
  /// Names are compared case-insensitively: Postgres folds an unquoted identifier to lower case, so a caller who
  /// writes the name as a migration spells it must not silently assert nothing.
  /// </remarks>
  /// <exception cref="ArgumentException">No relation was named, which would assert nothing.</exception>
  /// <exception cref="QueryPlanAssertionException">One of them is sequentially scanned.</exception>
  public QueryPlan MustNotSequentiallyScan(params string[] relations) {
    ArgumentNullException.ThrowIfNull(relations);
    if (relations.Length == 0) {
      throw new ArgumentException(
        "Name at least one relation. A rule over no relations cannot fail.", nameof(relations));
    }

    foreach (var relation in relations) {
      foreach (var node in _nodes) {
        if (!node.NodeType.Equals("Seq Scan", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(node.RelationName, relation, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }
        throw new QueryPlanAssertionException(
          $"{relation} is read by a sequential scan: {node.ActualRows} rows over {node.ActualLoops} loop(s), "
          + $"visiting {node.SharedHit + node.SharedRead} shared buffers. A scan repeated per row is a different "
          + "defect from one unavoidable scan, which is why the loop count is here.", Json);
      }
    }

    return this;
  }

  /// <summary>Asserts that the plan reads <paramref name="indexName"/>.</summary>
  /// <exception cref="QueryPlanAssertionException">
  /// It does not. The message lists the indexes the planner did choose, which distinguishes a missing index from
  /// one that exists but cannot be matched, or one simply not preferred.
  /// </exception>
  public QueryPlan MustUseIndex(string indexName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

    if (_nodes.Any(node => string.Equals(node.IndexName, indexName, StringComparison.OrdinalIgnoreCase))) {
      return this;
    }

    var used = _nodes.Where(n => n.IndexName is not null).Select(n => n.IndexName!).Distinct().ToList();
    var usedText = used.Count == 0 ? "none - the plan reads no index at all" : string.Join(", ", used);
    throw new QueryPlanAssertionException(
      $"The plan does not read {indexName}. Indexes used: {usedText}.", Json);
  }

  /// <summary>Asserts the whole query visited no more than <paramref name="maximum"/> shared buffers.</summary>
  /// <exception cref="QueryPlanAssertionException">It visited more.</exception>
  public QueryPlan MustVisitAtMostSharedBuffers(long maximum) {
    ArgumentOutOfRangeException.ThrowIfNegative(maximum);

    if (SharedBuffersVisited > maximum) {
      throw new QueryPlanAssertionException(
        $"The query visited {SharedBuffersVisited} shared buffers ({SharedBuffersHit} hit, {SharedBuffersRead} "
        + $"read), over the ceiling of {maximum}.", Json);
    }

    return this;
  }

  /// <summary>
  /// Asserts the query did not visit more than <paramref name="buffersPerReturnedRow"/> shared buffers for each
  /// row it returned.
  /// </summary>
  /// <remarks>
  /// This is the rule that separates a correct-but-ruinous plan from a healthy one, because the result is right
  /// either way. A plan returning no rows has no ratio, so it is judged on its buffers against the ceiling alone:
  /// a lookup that found nothing cheaply passes, and a table scan that found nothing does not.
  /// </remarks>
  /// <exception cref="QueryPlanAssertionException">It did.</exception>
  public QueryPlan MustNotAmplifyBeyond(long buffersPerReturnedRow) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buffersPerReturnedRow);

    var budget = ActualRows > 0 ? buffersPerReturnedRow * ActualRows : buffersPerReturnedRow;
    if (SharedBuffersVisited <= budget) {
      return this;
    }

    var perRow = ActualRows > 0
      ? (SharedBuffersVisited / ActualRows).ToString(CultureInfo.InvariantCulture)
      : SharedBuffersVisited.ToString(CultureInfo.InvariantCulture) + " (no rows returned)";
    throw new QueryPlanAssertionException(
      $"The query visited {SharedBuffersVisited} shared buffers to return {ActualRows} row(s): {perRow} per row, "
      + $"over the ceiling of {buffersPerReturnedRow}. A correct result at this cost is a scan nested inside a "
      + "per-row loop, not contention.", Json);
  }
}

/// <summary>One node of a captured query plan.</summary>
/// <remarks>
/// A plain class rather than a record: a positional record emits an <c>init</c> accessor for every property, and
/// nothing can reach them when the type is only ever built from its constructor. Get-only properties leave no
/// surface that cannot be exercised.
/// </remarks>
#pragma warning disable S107 // A plan node carries the nine attributes EXPLAIN reports for one node. Grouping
// them into sub-objects would read better here and worse at every use site, where a rule asks for one of them by
// name (node.ActualLoops, node.SharedHit). The shape follows EXPLAIN's, which is the point of the type.
public sealed class QueryPlanNode(
    string nodeType,
    string? relationName,
    string? indexName,
    string? functionName,
    long actualRows,
    long actualLoops,
    double actualTotalTimeMs,
    long sharedHit,
    long sharedRead) {
#pragma warning restore S107

  /// <summary>The planner's node type, e.g. <c>Seq Scan</c> or <c>Index Only Scan</c>.</summary>
  public string NodeType { get; } = nodeType;

  /// <summary>The table the node reads, when it reads one.</summary>
  public string? RelationName { get; } = relationName;

  /// <summary>The index the node reads, when it reads one.</summary>
  public string? IndexName { get; } = indexName;

  /// <summary>The function the node scans, for a <c>Function Scan</c>.</summary>
  public string? FunctionName { get; } = functionName;

  /// <summary>Rows the node produced per loop.</summary>
  public long ActualRows { get; } = actualRows;

  /// <summary>How many times the node ran. A scan repeated per row shows up here.</summary>
  public long ActualLoops { get; } = actualLoops;

  /// <summary>What the node took, in milliseconds.</summary>
  public double ActualTotalTimeMs { get; } = actualTotalTimeMs;

  /// <summary>Shared buffers the node found in cache.</summary>
  public long SharedHit { get; } = sharedHit;

  /// <summary>Shared buffers the node read from disk.</summary>
  public long SharedRead { get; } = sharedRead;
}

/// <summary>A query's plan did not meet a rule a test stated about it.</summary>
/// <remarks>Its own type so a suite can tell a plan regression from an ordinary assertion failure.</remarks>
public sealed class QueryPlanAssertionException : Exception {
  /// <summary>Creates the exception with no detail.</summary>
  public QueryPlanAssertionException() { }

  /// <summary>Creates the exception with a message.</summary>
  public QueryPlanAssertionException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and an inner cause.</summary>
  public QueryPlanAssertionException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>Creates the exception, appending the plan so the failure carries the planner's own output.</summary>
  public QueryPlanAssertionException(string message, string plan)
    : base(message + Environment.NewLine + "Plan:" + Environment.NewLine + plan) => Plan = plan;

  /// <summary>The raw <c>EXPLAIN</c> JSON, when the failure carried one.</summary>
  public string? Plan { get; }
}
