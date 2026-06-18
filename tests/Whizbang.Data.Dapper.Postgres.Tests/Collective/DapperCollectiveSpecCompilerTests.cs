#pragma warning disable CA1707

using System.Linq.Expressions;
using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Locks the Dapper SQL compiler for collective-event SET clauses. The
/// compiler translates the perspective's <see cref="ICollectiveSpec{TModel}"/>
/// LINQ into a <c>jsonb_set</c> chain over the <c>data</c> column. Real
/// Postgres integration (executing the compiled SQL against a
/// Testcontainer) is the natural Slice 9 follow-up — these tests pin
/// the SQL string shape and parameter dictionary so behavior is locked
/// before the runner consumes it.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class DapperCollectiveSpecCompilerTests {

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    PropertyNamingPolicy = null, // preserve PascalCase property names — they're the jsonb path
  };

  // ── Single SetProperty ─────────────────────────────────────────────────

  [Test]
  public async Task Compile_ConstantString_EmitsJsonbSetWithQuotedValueAsync() {
    var spec = _spec(s => s.SetProperty(j => j.Status, "Archived"));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("jsonb_set(data, '{Status}'")
      .Because("Top-level property selector must navigate to '{Status}' inside the data jsonb column.");
    await Assert.That(compiled.SqlFragment).Contains("::jsonb")
      .Because("jsonb_set requires the new value to be jsonb-typed; the cast is part of the canonical shape.");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(1);
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("\"Archived\"")
      .Because("JsonSerializer-string-encoded value: the bind value is the literal JSON token '\"Archived\"', not the raw string Archived.");
  }

  [Test]
  public async Task Compile_ConstantInt_EmitsJsonbSetWithNumericLiteralAsync() {
    var spec = _spec(s => s.SetProperty(j => j.ViewCount, 42));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("'{ViewCount}'");
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("42")
      .Because("Numeric values serialize to the JSON literal '42', not quoted.");
  }

  // ── Chained SetProperty (multiple props) ───────────────────────────────

  [Test]
  public async Task Compile_TwoChainedSetProperty_EmitsNestedJsonbSetAsync() {
    var spec = _spec(s => s
      .SetProperty(j => j.Status, "Archived")
      .SetProperty(j => j.ViewCount, 0));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    // The fragment should contain TWO jsonb_set calls — outer is the
    // second-emitted (chained) call, inner is the first.
    var openCount = compiled.SqlFragment.Split("jsonb_set(", StringSplitOptions.None).Length - 1;
    await Assert.That(openCount).IsEqualTo(2)
      .Because("Two SetProperty calls => two nested jsonb_set invocations on data.");
    await Assert.That(compiled.SqlFragment).Contains("'{Status}'");
    await Assert.That(compiled.SqlFragment).Contains("'{ViewCount}'");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(2);
  }

  // ── Defensive errors ───────────────────────────────────────────────────

  [Test]
  public async Task Compile_EmptySpec_ThrowsInvalidOperationAsync() {
    // Statement-body lambdas can't convert to Expression trees, so build
    // an empty Expression manually (a no-op Action lambda body).
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_jobModel>), "s");
    var empty = Expression.Lambda<Action<ICollectiveSetters<_jobModel>>>(
      Expression.Empty(), sParam);
    var spec = new _stubSpec(empty);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<InvalidOperationException>()
      .Because("A spec that mutates zero properties translates to a SQL UPDATE with no SET clause — that's a malformed handler, not a domain condition.");
  }

  [Test]
  public async Task Compile_ComputedValueExpression_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> body =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);
    var spec = new _stubSpec(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<NotSupportedException>()
      .Because("Slice 9 first cut supports constants only — computed expressions throw with a clear pointer to SpecKind = RawSql for the escape hatch.");
  }

  [Test]
  public async Task Compile_NestedPropertyPath_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_complexModel>>> body =
      s => s.SetProperty(j => j.Nested.Inner, "v");
    var spec = new _stubSpecComplex(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_complexModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<NotSupportedException>()
      .Because("Nested paths require multi-level jsonb_set composition — out of scope for the first cut.");
  }

  [Test]
  public async Task Compile_NullSpec_ThrowsArgumentNullAsync() {
    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(null!, _jsonOptions))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Compile_NullJsonOptions_ThrowsArgumentNullAsync() {
    var spec = _spec(s => s.SetProperty(j => j.Status, "X"));
    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Captured-local value sources ──────────────────────────────────────

  [Test]
  public async Task Compile_CapturedLocal_ResolvedAtCompileTimeAsync() {
    var statusValue = "Pending";
    var spec = _spec(s => s.SetProperty(j => j.Status, statusValue));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("\"Pending\"")
      .Because("Captured-local value bound at compile time, JSON-serialized into the parameter dictionary.");
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
  }

  private sealed class _complexModel {
    public _nested Nested { get; set; } = new();
  }
  private sealed class _nested {
    public string Inner { get; set; } = string.Empty;
  }

#pragma warning disable CA1859 // analyzer suggestion ignored — tests assert against the interface, not the concrete record
  private static ICollectiveSpec<_jobModel> _spec(Expression<Action<ICollectiveSetters<_jobModel>>> expr)
    => new _stubSpec(expr);
#pragma warning restore CA1859

  private sealed class _stubSpec(Expression<Action<ICollectiveSetters<_jobModel>>> setters) : ICollectiveSpec<_jobModel> {
    public Expression<Action<ICollectiveSetters<_jobModel>>> Setters { get; } = setters;
  }

  private sealed class _stubSpecComplex(Expression<Action<ICollectiveSetters<_complexModel>>> setters) : ICollectiveSpec<_complexModel> {
    public Expression<Action<ICollectiveSetters<_complexModel>>> Setters { get; } = setters;
  }
}
