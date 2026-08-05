using System;
using System.Text.Json;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Policy for what to do when a stored snapshot's serialization version does not match the
/// current serialization version on read (i.e. no in-version deserializer can read it directly).
/// </summary>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/SnapshotEnvelopeTests.cs</tests>
public enum SnapshotUpgradePolicy {
  /// <summary>Discard the stale snapshot and rebuild the perspective from events. Always correct
  /// (snapshots are a derived cache) — the safe default.</summary>
  RebuildFromEvents = 0,

  /// <summary>Treat a version mismatch as an error.</summary>
  None = 1,

  /// <summary>Upgrade a stream's snapshot via a registered versioned deserializer when it is next
  /// used. Until versioned deserializers exist this degrades safely to <see cref="RebuildFromEvents"/>.</summary>
  LazyUpcast = 2,

  /// <summary>Upgrade all snapshots to the current version at startup. Until versioned
  /// deserializers exist this degrades safely to <see cref="RebuildFromEvents"/>.</summary>
  UpgradeOnStartup = 3,
}

/// <summary>What the reader should do with a stored snapshot blob.</summary>
public enum SnapshotReadAction {
  /// <summary>The stored serialization version matches; deserialize <see cref="SnapshotReadResult.Model"/>.</summary>
  UseModel = 0,

  /// <summary>The snapshot is stale/unversioned; ignore it and rebuild from events.</summary>
  RebuildFromEvents = 1,
}

/// <summary>The outcome of reading a stored snapshot blob through a <see cref="SnapshotUpgradePolicy"/>.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Model">The inner model JSON when <see cref="Action"/> is <see cref="SnapshotReadAction.UseModel"/>.</param>
/// <param name="StoredSerializationVersion">The serialization version stamped on the blob, or <see cref="VersionedJsonEnvelope.LEGACY"/> for an unversioned (legacy) blob.</param>
public readonly record struct SnapshotReadResult(SnapshotReadAction Action, JsonElement Model, int StoredSerializationVersion);

/// <summary>
/// Snapshot-specific reader/writer over the general <see cref="VersionedJsonEnvelope"/>: stamps the
/// snapshot serialization version on write, and on read decides (per <see cref="SnapshotUpgradePolicy"/>)
/// whether the stored model can be used directly or the perspective must be rebuilt from events.
/// This is the snapshot consumer of the framework-wide serialization-version mechanism.
/// </summary>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/SnapshotEnvelopeTests.cs</tests>
public static class SnapshotEnvelope {
  /// <summary>Wraps a model JSON element in a snapshot envelope stamped with the serialization version.</summary>
  /// <param name="model">The serialized perspective model.</param>
  /// <param name="serializationVersion">The current serialization version to stamp (see <see cref="SerializationVersion.CURRENT"/>).</param>
  public static JsonDocument Wrap(JsonElement model, int serializationVersion) =>
    VersionedJsonEnvelope.Wrap(model, serializationVersion);

  /// <summary>
  /// Reads a stored snapshot blob, deciding whether the model can be used directly or the
  /// perspective must be rebuilt from events, per <paramref name="policy"/>.
  /// </summary>
  /// <param name="stored">The stored snapshot blob (versioned envelope, or a legacy raw model).</param>
  /// <param name="currentSerializationVersion">The current serialization version.</param>
  /// <param name="policy">What to do when no in-version deserializer can read the blob.</param>
  /// <exception cref="InvalidOperationException">When the version mismatches and the policy is <see cref="SnapshotUpgradePolicy.None"/>.</exception>
  public static SnapshotReadResult Read(JsonDocument stored, int currentSerializationVersion, SnapshotUpgradePolicy policy) {
    ArgumentNullException.ThrowIfNull(stored);

    var versioned = VersionedJsonEnvelope.TryRead(stored, out var storedVersion, out var model);

    if (versioned && storedVersion == currentSerializationVersion) {
      return new SnapshotReadResult(SnapshotReadAction.UseModel, model, storedVersion);
    }

    if (policy == SnapshotUpgradePolicy.None) {
      throw new InvalidOperationException(
        $"Snapshot serialization version {storedVersion} does not match current version " +
        $"{currentSerializationVersion} and SnapshotUpgradePolicy.None forbids automatic handling.");
    }

    // RebuildFromEvents (default) and the upgrade policies (no versioned deserializers yet)
    // all resolve to a safe rebuild from events.
    return new SnapshotReadResult(SnapshotReadAction.RebuildFromEvents, default, storedVersion);
  }
}
