#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Verifies <see cref="CollectiveSettersRewriter.CollectAssignments{TModel}"/>
/// walks the spec's model-level <c>SetProperty</c> calls and returns one
/// <see cref="CollectiveSettersRewriter.CollectiveSetterAssignment"/> per
/// mutated property, pre-serialized to the exact JSON text the jsonb column
/// stores. The adapter feeds these into a nested
/// <c>jsonb_set(data, '{PathName}', @value::jsonb)</c> UPDATE — one raw path
/// for every mapping (complex-JSON, scalar/polymorphic, null setters).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveSettersRewriterTests {

  // ── Constant value → single serialized assignment ─────────────────────

  [Test]
  public async Task CollectAssignments_ConstantValue_ProducesSerializedAssignmentAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived");

    var assignments = CollectiveSettersRewriter.CollectAssignments(source);

    await Assert.That(assignments.Count).IsEqualTo(1)
      .Because("One SetProperty call produces exactly one assignment.");
    await Assert.That(assignments[0].PathName).IsEqualTo("Status")
      .Because("The path name is the model property name — it becomes the jsonb_set path element '{Status}'.");
    await Assert.That(assignments[0].JsonValue).IsEqualTo("\"Archived\"")
      .Because("The value is pre-serialized to the JSON text the jsonb column stores; a string becomes a quoted JSON string.");
    await Assert.That(assignments[0].IsNull).IsFalse()
      .Because("A non-null constant is not a JSON null.");
  }

  // ── Multiple chained SetProperty → assignments in source order ─────────

  [Test]
  public async Task CollectAssignments_TwoChainedSetProperty_ProducesTwoAssignmentsAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived")
           .SetProperty(j => j.ViewCount, 0);

    var assignments = CollectiveSettersRewriter.CollectAssignments(source);

    await Assert.That(assignments.Count).IsEqualTo(2)
      .Because("Two source SetProperty calls map one-to-one to two assignments.");
    // Collected in source order (receiver visited before the outer call).
    await Assert.That(assignments[0].PathName).IsEqualTo("Status");
    await Assert.That(assignments[1].PathName).IsEqualTo("ViewCount");
    await Assert.That(assignments[1].JsonValue).IsEqualTo("0")
      .Because("An int constant serializes to a bare JSON number.");
  }

  // ── Null value → IsNull assignment (JSON null) ────────────────────────

  [Test]
  public async Task CollectAssignments_NullValue_MarksIsNullAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, (string?)null);

    var assignments = CollectiveSettersRewriter.CollectAssignments(source);

    await Assert.That(assignments.Count).IsEqualTo(1);
    await Assert.That(assignments[0].IsNull).IsTrue()
      .Because("A null setter must be carried as IsNull so the adapter writes JSON null, which EF Core 10 cannot express against a ComplexProperty().ToJson() sub-property via ExecuteUpdate.");
    await Assert.That(assignments[0].JsonValue).IsEqualTo("null")
      .Because("Null serializes to the JSON null literal.");
  }

  // ── Computed-value SetProperty unsupported (matches Dapper compiler) ───

  [Test]
  public async Task CollectAssignments_ComputedValue_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(source))
      .ThrowsExactly<NotSupportedException>()
      .Because("v1.0 matches the Dapper compiler: constant-value SetProperty only. Computed expressions throw with a pointer to SpecKind = RawSql so the consumer is never silently surprised by an UPDATE that doesn't increment.");
  }

  // ── Empty spec rejection ──────────────────────────────────────────────

  [Test]
  public async Task CollectAssignments_EmptySpec_ThrowsInvalidOperationAsync() {
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_jobModel>), "s");
    var empty = Expression.Lambda<Action<ICollectiveSetters<_jobModel>>>(
      Expression.Empty(), sParam);

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(empty))
      .ThrowsExactly<InvalidOperationException>()
      .Because("A spec that mutates zero properties translates to a SQL UPDATE with no SET clause — that's a malformed handler.");
  }

  // ── Nested path selector rejection ────────────────────────────────────

  [Test]
  public async Task CollectAssignments_NestedPathSelector_ThrowsNotSupportedAsync() {
    // A two-hop member access (j => j.Nested.Inner) — the visitor must reject it at apply time.
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Nested.Inner, "x");

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(source))
      .ThrowsExactly<NotSupportedException>()
      .Because("Only scalar top-level selectors (j => j.Prop) are supported; nested paths require SpecKind = RawSql.");
  }

  // ── Null source defensive guard ────────────────────────────────────────

  [Test]
  public async Task CollectAssignments_NullSource_ThrowsArgumentNullAsync() {
    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments<_jobModel>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string? Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
    public _nested Nested { get; set; } = new();
  }

  private sealed class _nested {
    public string Inner { get; set; } = string.Empty;
  }
}
