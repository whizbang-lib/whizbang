using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A replayed event must carry the scope the store persisted for it.
/// </summary>
/// <remarks>
/// <para>
/// Perspective work is persisted as a reference — an event id and a perspective name — and the
/// worker rehydrates the event from the store to apply it. The store keeps the originating scope in
/// its own column, but it does NOT keep envelope metadata, so a rehydrated event has no hops.
/// </para>
/// <para>
/// Scope was only restored into hop zero when hops already existed. With none, the scope was read
/// from the row, deserialized successfully, and then silently dropped. <c>GetCurrentScope()</c>
/// walks hops, so it returned null, and any perspective with lifecycle receptors threw
/// <c>SecurityContextRequiredException</c> — on every event, forever.
/// </para>
/// <para>
/// The retry cannot succeed, which is what makes it expensive: ten attempts per event and then a
/// permanent park. One deployment accumulated 5,557 parked events and 1,762 exceptions while its
/// projection silently stopped converging. Nothing surfaced at the inbox level; the rows simply
/// showed as claimed and lease-expired.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/drain-mode</docs>
[Category("Integration")]
[Category("Shard3")]
public class ReplayScopeRehydrationTests : EFCoreTestBase {

  private static StreamEventData _row(string? scope, string? metadata) => new() {
    StreamId = Guid.Parse("2a2a2a2a-1111-4111-8111-111111111111"),
    EventId = Guid.Parse("2b2b2b2b-1111-4111-8111-111111111111"),
    EventType = typeof(ScopeProbeEvent).AssemblyQualifiedName!,
    EventData = """{"ProbeId":"2a2a2a2a-1111-4111-8111-111111111111"}""",
    Metadata = metadata,
    Scope = scope,
    CommitSequence = 1,
    EventWorkId = Guid.Parse("2c2c2c2c-1111-4111-8111-111111111111"),
  };

  // Serialized from the real type rather than hand-written: a hand-shaped literal that fails to
  // deserialize makes the row skip entirely, which looks like the bug under test rather than a
  // broken fixture.
  private static string ScopeJson() {
    var opts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-probe" };
    return System.Text.Json.JsonSerializer.Serialize(
      scope, opts.GetTypeInfo(typeof(Whizbang.Core.Lenses.PerspectiveScope)));
  }

  private EFCoreEventStore<WorkCoordinationDbContext> _store() =>
    new(CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task ScopeSurvivesReplayWhenTheEventHasNoHopsAsync() {
    // The production shape: the store has a scope column but no metadata column, so a rehydrated
    // event arrives with scope and no hops at all.
    var envelopes = _store().DeserializeStreamEvents(
      [_row(scope: ScopeJson(), metadata: null)], [typeof(ScopeProbeEvent)]);

    await Assert.That(envelopes.Count).IsEqualTo(1)
      .Because("precondition: the row must deserialize into an envelope at all");
    await Assert.That(envelopes[0].GetCurrentScope()).IsNotNull()
      .Because("the store persisted this event's scope and handed it back; dropping it because "
             + "there were no hops to hang it on is what makes a requiring-perspective unable to "
             + "apply any replayed event, permanently and without a retry that can succeed");
  }

  // NOTE: the hops-present path is deliberately not covered here. Building valid EnvelopeMetadata
  // JSON for a fixture proved unreliable (MessageHop has a custom converter with required members),
  // and a test that fails on fixture shape rather than behavior is worse than no test. That path is
  // unchanged by construction: the fix is an `else if (hops.Count == 0)` branch, so an event that
  // arrives WITH hops takes exactly the code it took before.

  [Test]
  public async Task AnEventWithNoStoredScopeStaysUnscopedAsync() {
    var envelopes = _store().DeserializeStreamEvents(
      [_row(scope: null, metadata: null)], [typeof(ScopeProbeEvent)]);

    await Assert.That(envelopes.Count).IsEqualTo(1);
    await Assert.That(envelopes[0].GetCurrentScope()).IsNull()
      .Because("inventing a scope for an event that genuinely has none would fabricate authority "
             + "the event never carried — the fix restores what was persisted, nothing more");
  }
}

/// <summary>Probe event for scope rehydration.</summary>
public record ScopeProbeEvent : IEvent {
  /// <summary>Stream this probe belongs to.</summary>
  [StreamId]
  public Guid ProbeId { get; init; }
}
