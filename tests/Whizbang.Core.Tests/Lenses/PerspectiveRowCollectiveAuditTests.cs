#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Locks the shape of <see cref="PerspectiveRow{TModel}.LastCollectiveEventId"/>
/// — the audit-pointer column added by the Slice 2 schema migration. The
/// adapter in Slice 6 already attempts to write this column via the
/// rewriter; Slice 7b wires the CLR property so the SQL UPDATE actually
/// reaches it.
/// </summary>
/// <remarks>
/// The property is nullable (<see cref="Nullable{Guid}"/>) — rows that
/// have never been touched by a collective event have it null; the
/// applier sets it on every affected row in the same SQL UPDATE that
/// performs the mutation, so audit visibility is atomic with the
/// state change.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class PerspectiveRowCollectiveAuditTests {

  [Test]
  public async Task PerspectiveRow_LastCollectiveEventId_DefaultsToNullAsync() {
    var row = _newRow();

    await Assert.That(row.LastCollectiveEventId).IsNull()
      .Because("Rows that have never been touched by a collective event must read as null — non-nullable would force every existing UPSERT path to invent a sentinel value, which is the kind of churn we explicitly ruled out in Slice 2.");
  }

  [Test]
  public async Task PerspectiveRow_LastCollectiveEventId_AcceptsGuidValueAsync() {
    var auditId = Guid.NewGuid();
    var row = _newRow();
    row.LastCollectiveEventId = auditId;

    await Assert.That(row.LastCollectiveEventId).IsEqualTo(auditId)
      .Because("The adapter's audit-write SetProperty<Guid?>(r => r.LastCollectiveEventId, eventId) needs a writable property; otherwise the audit-pointer step in Slice 6 silently no-ops at runtime.");
  }

  [Test]
  public async Task PerspectiveRow_LastCollectiveEventId_PropertyTypeIsNullableGuidAsync() {
    var prop = typeof(PerspectiveRow<_jobModel>).GetProperty(nameof(PerspectiveRow<_jobModel>.LastCollectiveEventId));

    await Assert.That(prop).IsNotNull()
      .Because("The Slice 6 adapter reflects on this property name; missing the property keeps the audit-write path dormant.");
    await Assert.That(prop!.PropertyType).IsEqualTo(typeof(Guid?))
      .Because("Nullable<Guid> matches the Slice 2 column nullability (last_collective_event_id uuid NULL).");
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
  }

  private static PerspectiveRow<_jobModel> _newRow() => new() {
    Id = Guid.NewGuid(),
    Data = new _jobModel(),
    Metadata = new PerspectiveMetadata(),
    Scope = new PerspectiveScope(),
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
    Version = 1,
  };
}
