#pragma warning disable CA1707

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Verifies <see cref="CollectiveSettersRewriter.Rewrite"/> translates
/// the spec's model-level <c>SetProperty</c> calls into the column-level
/// <c>SetProperty(r =&gt; r.Data, r =&gt; EF.Functions.JsonbSet(...))</c>
/// shape EF Core 10's <see cref="UpdateSettersBuilder{T}"/> can hand to
/// <c>ExecuteUpdateAsync</c>. EF 10 only supports top-level scalar
/// updates via <c>SetProperty</c>, so the adapter funnels every model
/// mutation through a single <c>data</c> column update with the
/// individual <c>SetProperty(j =&gt; j.X, value)</c> calls folded into a
/// chained <see cref="Functions.WhizbangJsonDbFunctions.JsonbSet{TData}"/>
/// expression that the custom translator emits as
/// <c>jsonb_set(...)</c> SQL.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveSettersRewriterTests {

  // ── Output shape: SetProperty(r => r.Data, r => ...) ──────────────────

  [Test]
  public async Task Rewrite_ConstantValue_ProducesSetPropertyOnDataColumnAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived");

    var rewritten = CollectiveSettersRewriter.Rewrite(source);

    await Assert.That(rewritten).IsNotNull();
    await Assert.That(rewritten.Parameters[0].Type)
      .IsEqualTo(typeof(UpdateSettersBuilder<PerspectiveRow<_jobModel>>))
      .Because("EF Core's ExecuteUpdateAsync expects the setters delegate to take UpdateSettersBuilder<PerspectiveRow<TModel>> — anything else won't translate.");

    var text = rewritten.ToString();
    await Assert.That(text).Contains("SetProperty")
      .Because("Result must be a SetProperty call on the UpdateSettersBuilder.");
    await Assert.That(text).Contains("r.Data")
      .Because("The LHS selector targets the Data jsonb column itself (r => r.Data), not r.Data.X — EF 10 rejects nested-path SetProperty.");
    await Assert.That(text).Contains("JsonbSet")
      .Because("The RHS uses EF.Functions.JsonbSet so the translator emits jsonb_set(...) SQL.");
    await Assert.That(text).Contains("\"Status\"")
      .Because("The constant property name is baked into the rewritten expression for the translator to render as the SQL path '{Status}'.");
    await Assert.That(text).Contains("Archived")
      .Because("The value is JSON-serialized into the constant string passed to JsonbSet — the SQL site casts it to jsonb. Exact ToString() formatting of the embedded quotes varies by .NET version; just verify the raw value appears.");
  }

  // ── Multiple chained SetProperty → nested JsonbSet ────────────────────

  [Test]
  public async Task Rewrite_TwoChainedSetProperty_FoldsIntoNestedJsonbSetAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived")
           .SetProperty(j => j.ViewCount, 0);

    var rewritten = CollectiveSettersRewriter.Rewrite(source);
    var text = rewritten.ToString();

    // Both property names appear in the rewritten body.
    await Assert.That(text).Contains("\"Status\"");
    await Assert.That(text).Contains("\"ViewCount\"");

    // Two JsonbSet calls fold into a nested chain.
    var occurrences = text.Split("JsonbSet", StringSplitOptions.None).Length - 1;
    await Assert.That(occurrences).IsEqualTo(2)
      .Because("Two source SetProperty calls fold into TWO nested JsonbSet invocations on r.Data.");
  }

  // ── Computed-value SetProperty unsupported (matches Slice 9) ──────────

  [Test]
  public async Task Rewrite_ComputedValue_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);

    await Assert.That(() => CollectiveSettersRewriter.Rewrite(source))
      .ThrowsExactly<NotSupportedException>()
      .Because("Slice 6 v1.0 matches Slice 9 Dapper: constant-value SetProperty only. Computed expressions throw with a pointer to SpecKind = RawSql so the consumer is never silently surprised by an UPDATE that doesn't increment.");
  }

  // ── Empty spec rejection ──────────────────────────────────────────────

  [Test]
  public async Task Rewrite_EmptySpec_ThrowsInvalidOperationAsync() {
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_jobModel>), "s");
    var empty = Expression.Lambda<Action<ICollectiveSetters<_jobModel>>>(
      Expression.Empty(), sParam);

    await Assert.That(() => CollectiveSettersRewriter.Rewrite(empty))
      .ThrowsExactly<InvalidOperationException>()
      .Because("A spec that mutates zero properties translates to a SQL UPDATE with no SET clause — that's a malformed handler.");
  }

  // ── Null source defensive guard ────────────────────────────────────────

  [Test]
  public async Task Rewrite_NullSource_ThrowsArgumentNullAsync() {
    await Assert.That(() => CollectiveSettersRewriter.Rewrite<_jobModel>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
  }
}
