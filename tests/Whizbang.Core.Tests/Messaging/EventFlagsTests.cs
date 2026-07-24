#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="EventFlags"/> bitmask shape. The framework
/// dispatch sites check individual flags by bitwise AND; the schema
/// stores the value as a single INTEGER column. Renaming a value or
/// changing its underlying bit position is a breaking change — these
/// tests pin them.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class EventFlagsTests {

  [Test]
  public async Task EventFlags_Compacted_BitPositionLockedAsync() {
    await Assert.That(_asInt(EventFlags.Compacted)).IsEqualTo(16)
      .Because("Bit position 4 — the permanent StateBased lifecycle (E3). Distinct from Ephemeral (8, self-destruct) so the reaper (flags&8) never touches a compacted origin.");
  }

  [Test]
  public async Task IsStateBased_TrueForEphemeralAndCompacted_FalseOtherwiseAsync() {
    await Assert.That(EventFlags.Ephemeral.IsStateBased()).IsTrue()
      .Because("Ephemeral is StateBased (no-replay) — self-destructing.");
    await Assert.That(EventFlags.Compacted.IsStateBased()).IsTrue()
      .Because("Compacted is StateBased (no-replay) — permanent.");
    await Assert.That((EventFlags.Ephemeral | EventFlags.Composite).IsStateBased()).IsTrue()
      .Because("StateBased is a bit test — other flags coexisting don't clear it.");
    await Assert.That(EventFlags.None.IsStateBased()).IsFalse()
      .Because("A Sourced event (no flags) is replayable, not StateBased.");
    await Assert.That(EventFlags.Composite.IsStateBased()).IsFalse()
      .Because("Composite is orthogonal — not a StateBased lifecycle flag.");
  }

  [Test]
  public async Task EventFlags_None_IsZeroAsync() {
    var value = _asInt(EventFlags.None);
    await Assert.That(value).IsEqualTo(0)
      .Because("None must be 0 so the default column value (DEFAULT 0) maps cleanly to 'no flags set' and the bitwise AND with any flag is 0.");
  }

  [Test]
  public async Task EventFlags_Collective_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.Collective);
    await Assert.That(value).IsEqualTo(1)
      .Because("Bit position 0. Production rows already carry this value once Slice 2' lands; renaming or moving it changes the column's meaning silently.");
  }

  [Test]
  public async Task EventFlags_Composite_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.Composite);
    await Assert.That(value).IsEqualTo(2)
      .Because("Bit position 1. Same backward-compat reasoning as Collective.");
  }

  [Test]
  public async Task EventFlags_NoRebroadcast_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.NoRebroadcast);
    await Assert.That(value).IsEqualTo(4)
      .Because("Bit position 2 — the first 'treatment' flag (as opposed to a 'category' flag). The outbox-enqueue guard reads this exact bit; moving it silently breaks no-rebroadcast suppression and any persisted wh_inbox.flags rows.");
  }

  [Test]
  public async Task EventFlags_Ephemeral_BitPositionLockedAsync() {
    var value = _asInt(EventFlags.Ephemeral);
    await Assert.That(value).IsEqualTo(8)
      .Because("Bit position 3 — the persisted 'this event is ephemeral' marker the emit chain reads to offload the body to wh_event_body and the reaper reads to gate consumption-based deletion. Moving it silently breaks body-offload routing and any persisted wh_event_store.flags rows.");
  }

  private static int _asInt(EventFlags f) => (int)f;

  [Test]
  public async Task EventFlags_Combine_DistinctBitsCoexistAsync() {
    // Category + treatment flags coexist: a fan-out child of a composite can be both. The framework
    // checks each bit independently, so OR-ing must keep them distinct.
    var combined = EventFlags.Collective | EventFlags.Composite | EventFlags.NoRebroadcast | EventFlags.Ephemeral;

    await Assert.That(combined.HasFlag(EventFlags.Collective)).IsTrue();
    await Assert.That(combined.HasFlag(EventFlags.Composite)).IsTrue();
    await Assert.That(combined.HasFlag(EventFlags.NoRebroadcast)).IsTrue();
    await Assert.That(combined.HasFlag(EventFlags.Ephemeral)).IsTrue();
    await Assert.That(_asInt(combined)).IsEqualTo(15)
      .Because("OR-ing 1|2|4|8 gives 0b1111 = 15. Catches accidental bit-collision between any two flags.");
  }

  [Test]
  public async Task EventFlags_HasFlag_NoneAlwaysReturnsTrueForNoneAsync() {
    await Assert.That(EventFlags.None.HasFlag(EventFlags.None)).IsTrue()
      .Because(".NET's HasFlag semantics — every flags value 'has' None. Confirms the test against the .NET behavior the dispatch site relies on.");
  }
}
