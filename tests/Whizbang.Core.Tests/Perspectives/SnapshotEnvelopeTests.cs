using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="SnapshotEnvelope"/> — the version stamp + read-policy decision that
/// makes a stale-format snapshot detectable instead of a silent misparse. Pure (no I/O):
/// operates on <see cref="JsonDocument"/>.
/// </summary>
public class SnapshotEnvelopeTests {
  [Test]
  public async Task Wrap_ThenRead_WithMatchingVersion_UsesModelAsync() {
    using var model = JsonDocument.Parse("""{"Count":7}""");
    using var stored = SnapshotEnvelope.Wrap(model.RootElement, serializationVersion: 3);

    var result = SnapshotEnvelope.Read(stored, currentSerializationVersion: 3, SnapshotUpgradePolicy.RebuildFromEvents);

    await Assert.That(result.Action).IsEqualTo(SnapshotReadAction.UseModel);
    await Assert.That(result.StoredSerializationVersion).IsEqualTo(3);
    await Assert.That(result.Model.GetProperty("Count").GetInt32()).IsEqualTo(7);
  }

  [Test]
  public async Task Read_WithOlderVersion_RebuildFromEvents_SignalsRebuildAsync() {
    using var model = JsonDocument.Parse("""{"Count":1}""");
    using var stored = SnapshotEnvelope.Wrap(model.RootElement, serializationVersion: 1);

    var result = SnapshotEnvelope.Read(stored, currentSerializationVersion: 2, SnapshotUpgradePolicy.RebuildFromEvents);

    await Assert.That(result.Action).IsEqualTo(SnapshotReadAction.RebuildFromEvents);
    await Assert.That(result.StoredSerializationVersion).IsEqualTo(1);
  }

  [Test]
  public async Task Read_WithVersionMismatch_NonePolicy_ThrowsAsync() {
    using var model = JsonDocument.Parse("""{"Count":1}""");
    using var stored = SnapshotEnvelope.Wrap(model.RootElement, serializationVersion: 1);

    await Assert.That(() => SnapshotEnvelope.Read(stored, currentSerializationVersion: 2, SnapshotUpgradePolicy.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Read_LegacyUnversionedBlob_RebuildFromEvents_SignalsRebuildAsync() {
    // A raw model blob written before versioning existed has no envelope wrapper.
    using var legacyRaw = JsonDocument.Parse("""{"Count":42}""");

    var result = SnapshotEnvelope.Read(legacyRaw, currentSerializationVersion: 1, SnapshotUpgradePolicy.RebuildFromEvents);

    await Assert.That(result.Action).IsEqualTo(SnapshotReadAction.RebuildFromEvents);
  }

  [Test]
  public async Task Read_VersionMismatch_UpcastPolicies_FallBackToRebuildForNowAsync() {
    // Snapshot-upcasters are not implemented yet; the upgrade policies safely degrade to
    // RebuildFromEvents (always correct — snapshots are derived from events).
    using var model = JsonDocument.Parse("""{"Count":1}""");
    using var stored = SnapshotEnvelope.Wrap(model.RootElement, serializationVersion: 1);

    var lazy = SnapshotEnvelope.Read(stored, 2, SnapshotUpgradePolicy.LazyUpcast);
    var startup = SnapshotEnvelope.Read(stored, 2, SnapshotUpgradePolicy.UpgradeOnStartup);

    await Assert.That(lazy.Action).IsEqualTo(SnapshotReadAction.RebuildFromEvents);
    await Assert.That(startup.Action).IsEqualTo(SnapshotReadAction.RebuildFromEvents);
  }
}
