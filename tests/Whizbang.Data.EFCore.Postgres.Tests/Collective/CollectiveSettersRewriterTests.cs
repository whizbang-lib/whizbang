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
/// the spec's model-level <c>SetProperty</c> calls into a fluent chain of
/// native <c>SetProperty(r =&gt; r.Data.&lt;Prop&gt;, value)</c> calls that
/// EF Core 10's <see cref="UpdateSettersBuilder{T}"/> hands to
/// <c>ExecuteUpdateAsync</c>. The turnkey perspective mapping declares
/// <c>Data</c> as a <c>ComplexProperty().ToJson()</c> complex type, for
/// which EF Core 10 natively supports nested JSON sub-property updates and
/// emits the appropriate <c>jsonb_set</c> SQL itself — no custom translator.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveSettersRewriterTests {

  // ── Output shape: SetProperty(r => r.Data.X, value) ──────────────────

  [Test]
  public async Task Rewrite_ConstantValue_ProducesNativeNestedSetPropertyAsync() {
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
    await Assert.That(text).Contains("r.Data.Status")
      .Because("The selector targets the JSON sub-property natively (r => r.Data.Status). EF Core 10 updates the ComplexProperty().ToJson() sub-property directly.");
    await Assert.That(text).DoesNotContain("JsonbSet")
      .Because("The native nested form replaces the custom JsonbSet translator entirely.");
    await Assert.That(text).Contains("Archived")
      .Because("The constant value is embedded directly. Exact ToString() formatting varies by .NET version; just verify the raw value appears.");
  }

  // ── Multiple chained SetProperty → chained native setters ─────────────

  [Test]
  public async Task Rewrite_TwoChainedSetProperty_ProducesTwoNativeSettersAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived")
           .SetProperty(j => j.ViewCount, 0);

    var rewritten = CollectiveSettersRewriter.Rewrite(source);
    var text = rewritten.ToString();

    // Both nested sub-property selectors appear in the rewritten body.
    await Assert.That(text).Contains("r.Data.Status");
    await Assert.That(text).Contains("r.Data.ViewCount");

    // Two source SetProperty calls become TWO native SetProperty calls (fluent chain).
    var occurrences = text.Split("SetProperty", StringSplitOptions.None).Length - 1;
    await Assert.That(occurrences).IsEqualTo(2)
      .Because("Two source SetProperty calls map one-to-one to TWO native SetProperty calls on r.Data sub-properties.");
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
