// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Storage cleanup and stream integrity, together (#2342). Every case is one (cleanup path, side, integrity mechanism)
/// pair over TWO real Postgres stores, an origin and a consumer of its events, with the real integrity receptors
/// between them. A case runs the cleanup on the origin, on the consumer or on both, then runs the integrity mechanism for
/// three cycles, re-running the cleanup between cycles the way maintenance does, and asserts two things:
/// <list type="bullet">
///   <item><b>Converges</b>: outside the control stream, no repair request, no divergence report, no redelivered event
///   stored again, no stored digest that differs from a fresh fold over the events still eligible to count, and no
///   drift found by the heal or the epoch verification.</item>
///   <item><b>Positive control</b>: a genuine loss beside the cleanup (an event of the same type the consumer never
///   received, or a damaged bucket or seal) IS found, and where the mechanism repairs, repaired. Without it, the
///   "nothing happened" assertions would also pass if the mechanism never ran.</item>
/// </list>
/// The pairs that are not meaningful, and why, are listed on the stream-integrity page
/// (resilience/stream-integrity#cleanup-and-integrity).
/// </summary>
/// <docs>resilience/stream-integrity#cleanup-and-integrity</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class CleanupIntegrityMatrixTests : EFCoreTestBase {
  private const string ORIGIN_NAME = "origin-svc";
  private const string CONSUMER_NAME = "consumer-svc";
  private const string ORIGIN_REQUEST_TOPIC = "origin.requests";
  private const string CONSUMER_TOPIC = "consumer.inbox";

  /// <summary>A plain durable event type.</summary>
  internal const string DURABLE = "Matrix.Contracts.OrderPlaced, Matrix.Contracts";
  /// <summary>
  /// A superseding (latest-wins) event type, keyed: a registered type, so a redelivered copy's key is read from its
  /// payload by the generated accessor, as on a real consumer (#2350).
  /// </summary>
  internal static readonly string SUPERSEDING = Whizbang.Core.TypeNameFormatter.Format(typeof(SupersedingProbeEmbedded));
  /// <summary>An ephemeral event type.</summary>
  internal const string EPHEMERAL = "Matrix.Contracts.CursorMoved, Matrix.Contracts";
  /// <summary>A durable type that is later declared ephemeral (reclassification).</summary>
  internal const string LATER_EPHEMERAL = "Matrix.Contracts.PresencePinged, Matrix.Contracts";
  /// <summary>A durable type no cleanup touches: history that pushes the sealed frontier past what a cleanup removes.</summary>
  internal const string FILLER = "Matrix.Contracts.LedgerNoted, Matrix.Contracts";
  /// <summary>A schedule occurrence type delivered at most once.</summary>
  internal const string AT_MOST_ONCE = "Matrix.Contracts.ReminderFired, Matrix.Contracts";

  private const int FLAG_EPHEMERAL = 8;
  private const int FLAG_AT_MOST_ONCE = 32;
  private const int FLAG_TERMINAL = 64;
  private const int FLAG_SUPERSEDING = 128;

  /// <summary>Far enough in the past that every horizon a cleanup measures on the database clock has passed.</summary>
  private static readonly DateTimeOffset _t0 = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

  // ===== The matrix =====

  /// <summary>Every meaningful (cleanup, side, mechanism) pair. The excluded pairs are listed, with the reason, on the
  /// docs page (resilience/stream-integrity#cleanup-and-integrity).</summary>
  public static IEnumerable<Func<MatrixCase>> Cases() {
    foreach (var cleanup in Enum.GetValues<CleanupPath>()) {
      foreach (var side in Enum.GetValues<CleanupSide>()) {
        foreach (var mechanism in Enum.GetValues<IntegrityMechanism>()) {
          var c = new MatrixCase(cleanup, side, mechanism);
          if (c.IsMeaningful) {
            yield return () => c;
          }
        }
      }
    }
  }

  [Test]
  [MethodDataSource(nameof(Cases))]
  public async Task CleanupAndIntegrity_Converge_AndAGenuineLossBesideTheCleanupIsStillRepairedAsync(MatrixCase pair) {
    await using var h = await Harness.CreateAsync(this, pair);
    var scenario = Scenario.For(pair.Cleanup);

    // Both sides hold the same history, delivered live.
    await scenario.SeedAsync(h);
    // #2363: a cut removes only history its service has sealed and verified (the cut gate is on, as the turnkey
    // registration writes it while sealing runs), so the history is sealed on both sides and audited clean first.
    await h.SealAndVerifyAsync();

    // The cleanup, on the side(s) under test. It must change the store it runs on, or every "nothing happened"
    // assertion below would pass for a cleanup that did nothing.
    var originBefore = await Harness.FingerprintAsync(h.Origin, scenario.Streams);
    var consumerBefore = await Harness.FingerprintAsync(h.Consumer, scenario.Streams);
    await scenario.CleanAsync(h, pair.Side);
    if (scenario.ChangesTheStore) {
      if (pair.Side is CleanupSide.Origin or CleanupSide.Both) {
        await Assert.That(await Harness.FingerprintAsync(h.Origin, scenario.Streams)).IsNotEqualTo(originBefore)
          .Because($"[{pair}] precondition: the cleanup must remove or reclassify something on the origin");
      }
      if (pair.Side is CleanupSide.Consumer or CleanupSide.Both) {
        await Assert.That(await Harness.FingerprintAsync(h.Consumer, scenario.Streams)).IsNotEqualTo(consumerBefore)
          .Because($"[{pair}] precondition: the cleanup must remove or reclassify something on the consumer");
      }
    }

    // The positive control: an event of the cleanup's own type the consumer never received.
    var control = await scenario.DropOneAsync(h);

    switch (pair.Mechanism) {
      case IntegrityMechanism.StoredDigestHeal:
        await _assertHealConvergesAsync(h, pair, scenario, control);
        break;
      case IntegrityMechanism.EpochSeal:
        await _assertEpochsConvergeAsync(h, pair, scenario);
        break;
      case IntegrityMechanism.OperatorRedelivery:
        await _assertOperatorRedeliveryConvergesAsync(h, pair, scenario, control);
        break;
      default:
        await _assertAuditConvergesAsync(h, pair, scenario, control);
        break;
    }
  }

  // ===== The mechanisms =====

  /// <summary>
  /// The audit: ask, compare, drill down, repair, redeliver, store; three cycles, the cleanup re-run between them. The
  /// control stream must be repaired in the first cycle; nothing else may be asked for, reported or stored again.
  /// </summary>
  private static async Task _assertAuditConvergesAsync(Harness h, MatrixCase pair, Scenario scenario, Guid control) {
    var perCycle = new List<CycleTally>();
    for (var cycle = 1; cycle <= 3; cycle++) {
      var before = h.Tally(control);
      await h.AuditAsync(pair.Mechanism);
      perCycle.Add(h.Tally(control) - before);
      // Maintenance runs again between audits: whatever a redelivery stored, the cleanup meets again.
      await scenario.CleanAsync(h, pair.Side);
    }

    await Assert.That(await h.ConsumerHoldsAsync(control)).IsTrue()
      .Because($"[{pair}] positive control: the event the consumer never received must be found and repaired");
    await Assert.That(perCycle.Sum(c => c.ControlRepairs)).IsGreaterThanOrEqualTo(1)
      .Because($"[{pair}] positive control: the loss must be repaired by a repair request, not by accident");
    await Assert.That(string.Join(" ", perCycle.Select((c, i) => $"c{i + 1}{c}")))
      .IsEqualTo(string.Join(" ", perCycle.Select((_, i) => $"c{i + 1}{CycleTally.Quiet}")))
      .Because($"[{pair}] outside the control stream the cleanup must be invisible to the audit: no repair request, "
             + "no divergence report and no redelivered event stored again, in any cycle");
    await h.AssertDigestsFollowEligibilityAsync(pair);
  }

  /// <summary>The stored-digest heal and verification sweep finds nothing on either side, then finds the planted drift.</summary>
  private static async Task _assertHealConvergesAsync(Harness h, MatrixCase pair, Scenario scenario, Guid control) {
    _ = scenario;
    _ = control;
    var origin = await h.VerifyDigestTableAsync(h.Origin);
    var consumer = await h.VerifyDigestTableAsync(h.Consumer);
    await Assert.That((origin.DriftUpdated, origin.DriftRemoved, origin.DriftAdded)).IsEqualTo((0, 0, 0))
      .Because($"[{pair}] the origin's heal must find its digests already true after the cleanup");
    await Assert.That((consumer.DriftUpdated, consumer.DriftRemoved, consumer.DriftAdded)).IsEqualTo((0, 0, 0))
      .Because($"[{pair}] the consumer's heal must find its digests already true after the cleanup");
    await h.AssertDigestsFollowEligibilityAsync(pair);

    // Positive control: an event lost from the consumer's store without its digest is drift the heal must find.
    await Harness.LoseOneEventUnaccountedAsync(h.Consumer, scenario.ControlType);
    var planted = await h.VerifyDigestTableAsync(h.Consumer);
    await Assert.That(planted.DriftUpdated + planted.DriftRemoved).IsEqualTo(1)
      .Because($"[{pair}] positive control: the heal must find the one bucket a lost event left wrong");
  }

  /// <summary>The epoch verification finds no drift on either side after the cleanup, then finds a planted bad seal.</summary>
  private static async Task _assertEpochsConvergeAsync(Harness h, MatrixCase pair, Scenario scenario) {
    await Harness.CloseEpochsAsync(h.Origin);
    await Harness.CloseEpochsAsync(h.Consumer);
    var (originChecked, originDrifted) = await Harness.VerifyEpochsAsync(h.Origin);
    var (_, consumerDrifted) = await Harness.VerifyEpochsAsync(h.Consumer);
    await Assert.That(originChecked).IsGreaterThan(0)
      .Because($"[{pair}] precondition: the origin has sealed epochs to verify");
    await Assert.That((originDrifted, consumerDrifted)).IsEqualTo((0, 0))
      .Because($"[{pair}] a cleanup never moves a seal: what a cut removed still counts in the epoch it sat in (#2363)");

    // Positive control: a damaged seal is found.
    await Harness.CorruptOneEpochAsync(h.Origin, scenario.ControlType);
    var (_, plantedDrifted) = await Harness.VerifyEpochsAsync(h.Origin);
    await Assert.That(plantedDrifted).IsEqualTo(1)
      .Because($"[{pair}] positive control: the verification must find the one seal planted wrong");
  }

  /// <summary>
  /// An operator redelivers every stream of the scenario. The consumer stores what it lacks, maintenance runs, and the
  /// consumer ends holding exactly what it held after its cleanup, plus the control event.
  /// </summary>
  private static async Task _assertOperatorRedeliveryConvergesAsync(Harness h, MatrixCase pair, Scenario scenario, Guid control) {
    var heldBefore = await Harness.HeldEventsAsync(h.Consumer, scenario.Streams);
    var digestsBefore = await Harness.DigestRowsAsync(h.Consumer, scenario.Streams);

    await h.OperatorRedeliverAsync([.. scenario.Streams, scenario.ControlStream(control)], scenario.Types);
    await scenario.CleanAsync(h, pair.Side);
    await h.OperatorRedeliverAsync([.. scenario.Streams], scenario.Types);
    await scenario.CleanAsync(h, pair.Side);

    await Assert.That(await h.ConsumerHoldsAsync(control)).IsTrue()
      .Because($"[{pair}] positive control: the operator's redelivery must bring the event the consumer lacked");
    await Assert.That(string.Join(",", await Harness.HeldEventsAsync(h.Consumer, scenario.Streams)))
      .IsEqualTo(string.Join(",", heldBefore))
      .Because($"[{pair}] a redelivery of history the consumer deliberately removed must not bring it back for good: after "
             + "maintenance the consumer holds what it held after its own cleanup");
    await Assert.That(string.Join(",", await Harness.DigestRowsAsync(h.Consumer, scenario.Streams)))
      .IsEqualTo(string.Join(",", digestsBefore))
      .Because($"[{pair}] and its digests are what they were");
    await h.AssertDigestsFollowEligibilityAsync(pair);
  }

  // ===== The cases =====

  /// <summary>One pair of the matrix. Its name is the pair, so the test output names it.</summary>
  public sealed record MatrixCase(CleanupPath Cleanup, CleanupSide Side, IntegrityMechanism Mechanism) {
    /// <summary>
    /// Whether the pair can interact at all. A cleanup that never touches a digested event cannot meet a digest-based
    /// mechanism through a removal, so only its audit and redelivery pairs run (they still prove the type never reads as
    /// divergent); the reasons for each exclusion are on the docs page.
    /// </summary>
    public bool IsMeaningful {
      get {
        if (Cleanup == CleanupPath.RewindBookkeeping) {
          // Local records on the local lane, never on the wire: only the per-store mechanisms of the store that has them.
          return Side == CleanupSide.Origin && Mechanism is IntegrityMechanism.StoredDigestHeal or IntegrityMechanism.EpochSeal;
        }
        // Never digested: the epoch seal sees nothing of these events. The audit, heal and redelivery still run.
        return !(Mechanism == IntegrityMechanism.EpochSeal && Cleanup is CleanupPath.EphemeralBodyReap
          or CleanupPath.EphemeralPointerPrune or CleanupPath.AtMostOnce or CleanupPath.AuditRecords);
      }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Cleanup} | {Side} | {Mechanism}";
  }

  /// <summary>The cleanup paths of the matrix.</summary>
  public enum CleanupPath {
    /// <summary>The superseded-event reaper (perform_maintenance Task 16) removes an older copy of a key.</summary>
    SupersededReap,
    /// <summary>The ephemeral body reaper (Task 8) removes consumed ephemeral bodies.</summary>
    EphemeralBodyReap,
    /// <summary>The tier-2 pointer prune removes ancient ephemeral pointers whose body is gone.</summary>
    EphemeralPointerPrune,
    /// <summary>A durable type declared ephemeral: its stored history is reclassified.</summary>
    ReclassifyToEphemeral,
    /// <summary>
    /// The rebuild and rewind bookkeeping records (#2320): stored durable by earlier builds, in streams of their own on the
    /// local lane, then declared ephemeral; the history is reclassified and maintenance reaps and prunes it. Local
    /// only, never on the wire, so only the origin side and the per-store mechanisms are meaningful.
    /// </summary>
    RewindBookkeeping,
    /// <summary>A stream's prefix is truncated (wh_truncate_stream_prefix).</summary>
    PrefixTruncate,
    /// <summary>
    /// A stream's books are closed (a terminal event seals it on every side that stores it, as wh_close_books does through
    /// wh_append_event and wh_finalize_stream) and its prefix is then truncated.
    /// </summary>
    CloseTheBooks,
    /// <summary>A whole stream is archived out of the store.</summary>
    ArchiveWhole,
    /// <summary>A stream's prefix is archived out of the store.</summary>
    ArchivePrefix,
    /// <summary>A whole stream is archived and then restored.</summary>
    ArchiveThenRestore,
    /// <summary>A stream is purged.</summary>
    StreamPurge,
    /// <summary>At-most-once schedule occurrences, never folded on either side.</summary>
    AtMostOnce,
    /// <summary>Audit records, digest-exempt on every side.</summary>
    AuditRecords,
  }

  /// <summary>Where a cleanup runs.</summary>
  public enum CleanupSide {
    /// <summary>On the origin only.</summary>
    Origin,
    /// <summary>On the consumer only.</summary>
    Consumer,
    /// <summary>On both.</summary>
    Both,
  }

  /// <summary>The integrity mechanisms of the matrix.</summary>
  public enum IntegrityMechanism {
    /// <summary>The steady-state audit: a windowed type-level manifest from the consumer's seal, drill-down and repair.</summary>
    AuditTypesWindowed,
    /// <summary>
    /// The steady-state audit with the paced repair drain on, as a service runs by default: the compare records its
    /// deficits in the ledger, and one tick of the real drain sends the repair requests.
    /// </summary>
    AuditPacedDrain,
    /// <summary>The table-driven type-level manifest (no window), drill-down and repair.</summary>
    AuditTypesTable,
    /// <summary>A stream-level manifest straight away (the drill-down level), and repair.</summary>
    AuditStreams,
    /// <summary>The full sweep: recomputed folds over all history, drill-down and repair.</summary>
    AuditSweep,
    /// <summary>The stored-digest heal and verification sweep on each side.</summary>
    StoredDigestHeal,
    /// <summary>Epoch closure, refold and verification on each side.</summary>
    EpochSeal,
    /// <summary>An operator asks the origin to redeliver whole streams.</summary>
    OperatorRedelivery,
  }

  // ===== Scenarios: what each cleanup removes, and the loss planted beside it =====

  /// <summary>The history one cleanup path acts on, how it runs, and the control loss of its type.</summary>
  internal abstract class Scenario {
    /// <summary>The streams the cleanup acts on.</summary>
    public List<Guid> Streams { get; } = [];
    /// <summary>The event types the scenario stores.</summary>
    public abstract string[] Types { get; }
    /// <summary>The digested type the control loss is planted in.</summary>
    public abstract string ControlType { get; }
    /// <summary>
    /// Whether the cleanup leaves the store visibly changed. False for an archive followed by its restore (the store
    /// ends as it began) and for the never-digested kinds whose maintenance has nothing of theirs to remove (the case
    /// then proves only that the kind never reads as divergent).
    /// </summary>
    public virtual bool ChangesTheStore => true;
    private readonly Dictionary<Guid, Guid> _controlStreams = [];

    public static Scenario For(CleanupPath path) => path switch {
      CleanupPath.SupersededReap => new SupersededScenario(),
      CleanupPath.EphemeralBodyReap => new EphemeralScenario(prune: false),
      CleanupPath.EphemeralPointerPrune => new EphemeralScenario(prune: true),
      CleanupPath.ReclassifyToEphemeral => new ReclassifyScenario(),
      CleanupPath.RewindBookkeeping => new RewindBookkeepingScenario(),
      CleanupPath.PrefixTruncate => new PrefixScenario(PrefixKind.Truncate),
      CleanupPath.CloseTheBooks => new PrefixScenario(PrefixKind.Close),
      CleanupPath.ArchiveWhole => new PrefixScenario(PrefixKind.ArchiveWhole),
      CleanupPath.ArchivePrefix => new PrefixScenario(PrefixKind.ArchivePrefix),
      CleanupPath.ArchiveThenRestore => new PrefixScenario(PrefixKind.ArchiveThenRestore),
      CleanupPath.StreamPurge => new PrefixScenario(PrefixKind.Purge),
      CleanupPath.AtMostOnce => new FlaggedScenario(AT_MOST_ONCE, FLAG_AT_MOST_ONCE),
      CleanupPath.AuditRecords => new FlaggedScenario(Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames[0] + ", Whizbang.Core", 0),
      _ => throw new ArgumentOutOfRangeException(nameof(path), path, "no scenario"),
    };

    /// <summary>Stores the history on the origin and delivers it live to the consumer.</summary>
    public abstract Task SeedAsync(Harness h);

    /// <summary>Runs the cleanup on the side(s) named. Idempotent: maintenance runs it again between cycles.</summary>
    public async Task CleanAsync(Harness h, CleanupSide side) {
      if (side is CleanupSide.Origin or CleanupSide.Both) {
        await CleanOnAsync(h, h.Origin);
      }
      if (side is CleanupSide.Consumer or CleanupSide.Both) {
        await CleanOnAsync(h, h.Consumer);
      }
    }

    /// <summary>Runs the cleanup on one store.</summary>
    protected abstract Task CleanOnAsync(Harness h, Store store);

    /// <summary>Stores one event of <see cref="ControlType"/> on the origin, in a stream of its own, and never delivers it.</summary>
    public async Task<Guid> DropOneAsync(Harness h) {
      var stream = (Guid)TrackedGuid.New();
      var ids = await h.EmitAsync(new Emit(stream, ControlType, 0, null));
      _controlStreams[ids[0]] = stream;
      // A delivered sibling in the same stream, so the control bucket exists on both sides and only its count differs.
      var delivered = await h.EmitAsync(new Emit(stream, ControlType, 0, null));
      await h.DeliverAsync(delivered);
      return ids[0];
    }

    /// <summary>The stream the control event was stored in.</summary>
    public Guid ControlStream(Guid control) => _controlStreams[control];
  }

  private sealed class SupersededScenario : Scenario {
    public override string[] Types => [SUPERSEDING];
    public override string ControlType => SUPERSEDING;

    public override async Task SeedAsync(Harness h) {
      // Two streams, each with a key written once and a key replaced once (the older copy is superseded). The key
      // written once comes first, so the superseded copy is not the stream's first event.
      for (var i = 0; i < 2; i++) {
        var stream = (Guid)TrackedGuid.New();
        Streams.Add(stream);
        var ids = await h.EmitAsync(
          new Emit(stream, SUPERSEDING, FLAG_SUPERSEDING, "key-b"),
          new Emit(stream, SUPERSEDING, FLAG_SUPERSEDING, "key-a"));
        ids = [.. ids, .. await h.EmitAsync(new Emit(stream, SUPERSEDING, FLAG_SUPERSEDING, "key-a"))];
        await h.DeliverAsync(ids);
      }
    }

    protected override Task CleanOnAsync(Harness h, Store store) =>
      Harness.ExecuteAsync(store, "SELECT * FROM wh_reap_superseded_events(5000)");
  }

  private sealed class EphemeralScenario(bool prune) : Scenario {
    public override string[] Types => [EPHEMERAL, DURABLE];
    public override string ControlType => DURABLE;

    public override async Task SeedAsync(Harness h) {
      var stream = (Guid)TrackedGuid.New();
      Streams.Add(stream);
      var ids = await h.EmitAsync(
        new Emit(stream, EPHEMERAL, FLAG_EPHEMERAL, null),
        new Emit(stream, EPHEMERAL, FLAG_EPHEMERAL, null),
        new Emit(stream, EPHEMERAL, FLAG_EPHEMERAL, null));
      await h.DeliverAsync(ids);
      if (prune) {
        await Harness.SetSettingAsync(h.Origin, "ephemeral_deep_maintenance_enabled", "true", "boolean");
        await Harness.SetSettingAsync(h.Consumer, "ephemeral_deep_maintenance_enabled", "true", "boolean");
      }
    }

    protected override async Task CleanOnAsync(Harness h, Store store) {
      await Harness.ExecuteAsync(store, "SELECT * FROM perform_maintenance()");
      if (prune) {
        // The prune's own interval gate is cleared so every cycle may prune.
        await Harness.ExecuteAsync(store, "DELETE FROM wh_settings WHERE setting_key = 'ephemeral_pointer_prune_last_run'");
        await Harness.ExecuteAsync(store, "SELECT * FROM prune_ancient_ephemeral_pointers()");
      }
    }
  }

  private sealed class ReclassifyScenario : Scenario {
    public override string[] Types => [LATER_EPHEMERAL, DURABLE];
    public override string ControlType => DURABLE;

    public override async Task SeedAsync(Harness h) {
      var stream = (Guid)TrackedGuid.New();
      Streams.Add(stream);
      var ids = await h.EmitAsync(new Emit(stream, LATER_EPHEMERAL, 0, null), new Emit(stream, LATER_EPHEMERAL, 0, null));
      await h.DeliverAsync(ids);
    }

    protected override Task CleanOnAsync(Harness h, Store store) =>
      Harness.ExecuteAsync(store, "SELECT * FROM reclassify_events_ephemeral(@t)", ("t", new[] { LATER_EPHEMERAL }));
  }

  private sealed class RewindBookkeepingScenario : Scenario {
    private static readonly string[] _bookkeeping = [
      "Whizbang.Core.Events.System.PerspectiveRewindStarted, Whizbang.Core",
      "Whizbang.Core.Events.System.PerspectiveRewindCompleted, Whizbang.Core",
    ];

    public override string[] Types => [.. _bookkeeping, DURABLE];
    public override string ControlType => DURABLE;

    public override async Task SeedAsync(Harness h) {
      // Two rewinds by an earlier build: each in a stream of its own, stored durable and folded, never delivered.
      for (var i = 0; i < 2; i++) {
        var stream = (Guid)TrackedGuid.New();
        Streams.Add(stream);
        await h.EmitAsync(new Emit(stream, _bookkeeping[0], 0, null), new Emit(stream, _bookkeeping[1], 0, null));
      }
      await Harness.SetSettingAsync(h.Origin, "ephemeral_deep_maintenance_enabled", "true", "boolean");
    }

    protected override async Task CleanOnAsync(Harness h, Store store) {
      // The startup reconcile of the now-ephemeral family, then maintenance's reap and the pointer prune.
      await Harness.ExecuteAsync(store, "SELECT * FROM reclassify_events_ephemeral(@t)", ("t", _bookkeeping));
      await Harness.ExecuteAsync(store, "SELECT * FROM perform_maintenance()");
      await Harness.ExecuteAsync(store, "DELETE FROM wh_settings WHERE setting_key = 'ephemeral_pointer_prune_last_run'");
      await Harness.ExecuteAsync(store, "SELECT * FROM prune_ancient_ephemeral_pointers()");
    }
  }

  internal enum PrefixKind { Truncate, Close, ArchiveWhole, ArchivePrefix, ArchiveThenRestore, Purge }

  private sealed class PrefixScenario(PrefixKind kind) : Scenario {
    public override bool ChangesTheStore => kind != PrefixKind.ArchiveThenRestore;
    public override string[] Types => [DURABLE];
    public override string ControlType => DURABLE;

    public override async Task SeedAsync(Harness h) {
      var stream = (Guid)TrackedGuid.New();
      Streams.Add(stream);
      var ids = await h.EmitAsync(
        new Emit(stream, DURABLE, 0, null), new Emit(stream, DURABLE, 0, null), new Emit(stream, DURABLE, 0, null),
        new Emit(stream, DURABLE, kind == PrefixKind.Close ? FLAG_TERMINAL : 0, null));
      await h.DeliverAsync(ids);
    }

    protected override async Task CleanOnAsync(Harness h, Store store) {
      var stream = Streams[0];
      // The second event's version on this store: versions are a store's own positions.
      var through = await h.VersionOfNthAsync(store, stream, 2);
      switch (kind) {
        case PrefixKind.Truncate:
          if (through is long t) {
            await Harness.ExecuteAsync(store, "SELECT * FROM wh_truncate_stream_prefix(@s, @v)", ("s", stream), ("v", t));
          }
          break;
        case PrefixKind.Close:
          // The books were closed at the seed (the terminal event); what is removed afterwards is the prefix.
          if (through is long c) {
            await Harness.ExecuteAsync(store, "SELECT * FROM wh_truncate_stream_prefix(@s, @v)", ("s", stream), ("v", c));
          }
          break;
        case PrefixKind.ArchiveWhole:
          await h.ArchiveAsync(store, stream, null);
          break;
        case PrefixKind.ArchivePrefix:
          if (through is long a) {
            await h.ArchiveAsync(store, stream, a);
          }
          break;
        case PrefixKind.ArchiveThenRestore:
          await h.ArchiveAsync(store, stream, null);
          await Harness.ExecuteAsync(store, "SELECT * FROM wh_restore_archived_stream(@s)", ("s", stream));
          break;
        case PrefixKind.Purge:
          await Harness.ExecuteAsync(store, "SELECT * FROM wh_purge_streams(@s, false, @p, 0, 'matrix', 'matrix')",
            ("s", new[] { stream }), ("p", (Guid)TrackedGuid.New()));
          break;
      }
    }
  }

  private sealed class FlaggedScenario(string type, int flags) : Scenario {
    public override bool ChangesTheStore => false;
    public override string[] Types => [type, DURABLE];
    public override string ControlType => DURABLE;

    public override async Task SeedAsync(Harness h) {
      var stream = (Guid)TrackedGuid.New();
      Streams.Add(stream);
      var ids = await h.EmitAsync(new Emit(stream, type, flags, null), new Emit(stream, type, flags, null));
      await h.DeliverAsync(ids);
    }

    protected override Task CleanOnAsync(Harness h, Store store) => Harness.ExecuteAsync(store, "SELECT * FROM perform_maintenance()");
  }

  // ===== The harness =====

  /// <summary>An event to emit on the origin.</summary>
  internal readonly record struct Emit(Guid Stream, string Type, int Flags, string? Key);

  /// <summary>One side's store.</summary>
  internal sealed record Store(string Name, string ConnectionString, DbContextOptions<WorkCoordinationDbContext> Options);

  /// <summary>What one audit cycle did outside the control stream, and how often it repaired the control.</summary>
  internal readonly record struct CycleTally(int Repairs, int Reports, int Redelivered, int Restored, int ControlRepairs) {
    public static CycleTally Quiet => new(0, 0, 0, 0, 0);
    public static CycleTally operator -(CycleTally a, CycleTally b) =>
      new(a.Repairs - b.Repairs, a.Reports - b.Reports, a.Redelivered - b.Redelivered, a.Restored - b.Restored,
        a.ControlRepairs - b.ControlRepairs);
    /// <summary>Short, so all three cycles fit in an assertion message: requests, reports, redelivered, restored.</summary>
    public override string ToString() => $"[rq={Repairs} rp={Reports} rd={Redelivered} rs={Restored}]";
  }

  /// <summary>
  /// Two stores, the real origin receptors (manifest request, redelivery) and the real consumer manifest receptor, the
  /// wire between them, and the consumer's receive path: a redelivery bundle is expanded by the real composite fan-out
  /// and stored through the real inbox emit chain.
  /// </summary>
  internal sealed class Harness : IAsyncDisposable {
    private readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
    private readonly IntegrityWireTransport _originTransport = new();
    private readonly IntegrityWireTransport _consumerTransport = new();
    private readonly IntegrityWireDispatcher _consumerDispatcher = new();
    private readonly List<Guid> _restored = [];
    private readonly List<Guid> _redelivered = [];
    private readonly Dictionary<Guid, (Guid Stream, string Type, int Flags, string? Key, long Seq)> _emitted = [];
    private string _consumerDbName = "";
    private NpgsqlDataSource? _consumerDataSource;
    private ServiceProvider? _originProvider;
    private ServiceProvider? _consumerProvider;
    private IntegrityManifestRequestReceptor _originRequestReceptor = null!;
    private RedeliveryRequestReceptor _originRedeliveryReceptor = null!;
    private IntegrityManifestReceptor _consumerManifestReceptor = null!;
    private int _processedOrigin;
    private int _processedConsumer;
    private DateTimeOffset _clock = _t0;

    public Store Origin { get; private set; } = null!;
    public Store Consumer { get; private set; } = null!;
    public Guid OriginId { get; private set; }

    public static async Task<Harness> CreateAsync(CleanupIntegrityMatrixTests test, MatrixCase pair) {
      var h = new Harness {
        Origin = new Store("origin", test.ConnectionString, test.DbContextOptions),
        _consumerDbName = (await EFCoreSchemaTemplate.CreateDatabaseAsync("consumer")).Name,
      };
      var cs = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = h._consumerDbName,
        Timezone = "UTC",
        IncludeErrorDetail = true,
      }.ConnectionString;
      (h._consumerDataSource, var consumerOptions) = CreateContextOptions(cs);
      h.Consumer = new Store("consumer", cs, consumerOptions);
      h.OriginId = await h._localServiceIdAsync(h.Origin);

      var both = new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        AuditSettleWindowMinutes = 0,
        // Every cycle is news: no cooldown and no backoff hide a divergence or a repeated repair.
        DivergenceReportCooldownMinutes = 0,
        RepairRequestBackoffSeconds = 0,
        MaxRepairAttemptsPerBucket = 1000,
        PublishReportEvents = true,
        // The compare dispatches its repairs inline, so a cycle is one audit; with the paced drain on, the compare only
        // records its deficits and a drain tick in the cycle sends them.
        RepairDrainEnabled = pair.Mechanism == IntegrityMechanism.AuditPacedDrain,
        RepairDrainRatePerSecond = 1000,
      };
      h._originProvider = h._buildProvider(h.Origin, h._originTransport, new IntegrityWireDispatcher(), ORIGIN_NAME, both, tracker: null);
      var tracker = new IntegrityGapTracker();
      tracker.RecordCheckpoint(h.OriginId, ORIGIN_NAME, DateTimeOffset.UnixEpoch, ORIGIN_REQUEST_TOPIC);
      h._consumerProvider = h._buildProvider(h.Consumer, h._consumerTransport, h._consumerDispatcher, CONSUMER_NAME, both, tracker);
      h._originRequestReceptor = new IntegrityManifestRequestReceptor(
        h._originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);
      h._originRedeliveryReceptor = new RedeliveryRequestReceptor(
        h._originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
      h._consumerManifestReceptor = new IntegrityManifestReceptor(
        h._consumerProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
      return h;
    }

    public async ValueTask DisposeAsync() {
      if (_originProvider is not null) {
        await _originProvider.DisposeAsync();
      }
      if (_consumerProvider is not null) {
        await _consumerProvider.DisposeAsync();
      }
      if (_consumerDataSource is not null) {
        await _consumerDataSource.DisposeAsync();
      }
      if (_consumerDbName.Length > 0) {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(Consumer.ConnectionString));
        await PerTestDatabaseFactory.DropAsync(_consumerDbName);
      }
    }

    // ----- writing -----

    /// <summary>Emits the events on the origin through the outbox emit chain, as one batch, then stamps their commit
    /// sequences. Ids are ordered, so a later event of a key is also the newer one by id.</summary>
    public async Task<Guid[]> EmitAsync(params Emit[] events) {
      Guid[] ids = [.. Enumerable.Range(0, events.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
      _clock = _clock.AddMinutes(1);
      await using var conn = await _openAsync(Origin);
      var instanceId = (Guid)TrackedGuid.New();
      for (var i = 0; i < events.Length; i++) {
        await _executeAsync(conn, """
          INSERT INTO wh_outbox
            (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
             status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
          VALUES
            (@id, 'matrix-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('k', @key::text, 'DocumentId', @sid::text, 'Model', @key::text)),
             CASE WHEN @key::text IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('sk', @key::text) END, NULL,
             1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
          """,
          ("id", ids[i]), ("type", events[i].Type), ("key", (object?)events[i].Key ?? DBNull.Value), ("inst", instanceId),
          ("lease", _clock.AddMinutes(5)), ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", _clock));
      }
      await using (var emit = conn.CreateCommand()) {
        emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
        emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        emit.Parameters.AddWithValue("inst", instanceId);
        emit.Parameters.AddWithValue("lease", _clock.AddMinutes(5));
        emit.Parameters.AddWithValue("now", _clock);
        _ = await emit.ExecuteScalarAsync();
      }
      await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
      for (var i = 0; i < events.Length; i++) {
        var seq = await _scalarAsync(conn, "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", ("id", ids[i]));
        _emitted[ids[i]] = (events[i].Stream, events[i].Type, events[i].Flags, events[i].Key, seq);
      }
      return ids;
    }

    /// <summary>Delivers emitted events to the consumer live: through its inbox, with the key and flags the wire
    /// carries and the origin's identity and commit sequence.</summary>
    public Task DeliverAsync(IReadOnlyList<Guid> ids) =>
      _storeOnConsumerAsync([.. ids.Select(id => {
        var e = _emitted[id];
        return _inboxRow(id, e.Stream, e.Type, (EventFlags)e.Flags, e.Key, e.Seq, restoreIfMissing: false);
      })]);

    private InboxMessage _inboxRow(Guid id, Guid stream, string type, EventFlags flags, string? key, long seq, bool restoreIfMissing) {
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["k"] = key, ["DocumentId"] = stream.ToString(), ["Model"] = key }, _json.GetTypeInfo(typeof(Dictionary<string, string?>))),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        SourceServiceId = OriginId,
        SourceCommitSequence = seq,
      };
      return new InboxMessage {
        MessageId = id,
        HandlerName = "MatrixHandler",
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        MessageType = type,
        StreamId = stream,
        IsEvent = true,
        Flags = flags,
        Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [], SupersedingKey = key },
        SourceServiceId = OriginId,
        SourceCommitSequence = seq,
        RestoreIfMissing = restoreIfMissing,
      };
    }

    /// <summary>Stores rows in the consumer's inbox and runs the claim's emit chain over them; returns the ids that
    /// reached the event store.</summary>
    private async Task<List<Guid>> _storeOnConsumerAsync(InboxMessage[] rows, Func<Task>? afterInbox = null) {
      if (rows.Length == 0) {
        return [];
      }
      _clock = _clock.AddMinutes(1);
      await using var ctx = new WorkCoordinationDbContext(Consumer.Options);
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json);
      await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
      if (afterInbox is not null) {
        await afterInbox();
      }
      await using var conn = await _openAsync(Consumer);
      Guid[] ids = [.. rows.Select(r => r.MessageId)];
      var before = await _idsPresentAsync(conn, ids);
      var instanceId = (Guid)TrackedGuid.New();
      await _executeAsync(conn,
        "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
        ("inst", instanceId), ("ids", ids));
      await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', @now, 4)",
        ("inst", instanceId), ("now", _clock));
      // Settled inbox rows leave, so a later redelivery of the same id is judged by the event store, as in production.
      await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids)", ("ids", ids));
      await _executeAsync(conn, "DELETE FROM wh_inbox WHERE message_id = ANY(@ids)", ("ids", ids));
      await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
      var after = await _idsPresentAsync(conn, ids);
      return [.. after.Except(before)];
    }

    private static async Task<HashSet<Guid>> _idsPresentAsync(NpgsqlConnection conn, Guid[] ids) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE event_id = ANY(@ids)";
      cmd.Parameters.AddWithValue(nameof(ids), ids);
      var set = new HashSet<Guid>();
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        set.Add(reader.GetGuid(0));
      }
      return set;
    }

    // ----- the audit -----

    /// <summary>One audit cycle of the given kind, pumped until both sides are quiet.</summary>
    public async Task AuditAsync(IntegrityMechanism mechanism) {
      var ask = new RequestIntegrityManifest {
        RequesterService = CONSUMER_NAME,
        Topic = CONSUMER_TOPIC,
        EventTypes = [DURABLE, SUPERSEDING, EPHEMERAL, LATER_EPHEMERAL, AT_MOST_ONCE,
          Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames[0] + ", Whizbang.Core"],
        Level = mechanism == IntegrityMechanism.AuditStreams ? ManifestLevel.Streams : ManifestLevel.Types,
        Windowed = mechanism is IntegrityMechanism.AuditTypesWindowed or IntegrityMechanism.AuditPacedDrain,
        UseRecompute = mechanism == IntegrityMechanism.AuditSweep,
        SinceSequence = mechanism is IntegrityMechanism.AuditTypesWindowed or IntegrityMechanism.AuditPacedDrain
          ? await _sealAsync() : 0,
      };
      await _originRequestReceptor.HandleAsync(ask);
      await _pumpUntilQuietAsync();
      if (mechanism == IntegrityMechanism.AuditPacedDrain) {
        await _drainOnceAsync();
        await _pumpUntilQuietAsync();
      }
    }

    /// <summary>
    /// One tick of the consumer's real paced repair drain: it starts on a clock that signals each time the loop arms its
    /// wait, runs exactly one tick, and stops once that tick is over. The clock is far past every ledger row, so each
    /// recorded deficit is due.
    /// </summary>
    private async Task _drainOnceAsync() {
      var clock = new ArmSignalingClock(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
      using var worker = new RepairDrainWorker(
        _consumerProvider!.GetRequiredService<IServiceScopeFactory>(),
        _consumerProvider!.GetRequiredService<IOptions<StreamIntegrityOptions>>(),
        NullLogger<RepairDrainWorker>.Instance,
        clock);
      using var guard = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
      await worker.StartAsync(System.Threading.CancellationToken.None);
      try {
        await clock.Armed.WaitAsync(guard.Token);   // the loop waits out its first second
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.Armed.WaitAsync(guard.Token);   // the tick is over and the loop waits again
      } finally {
        await worker.StopAsync(System.Threading.CancellationToken.None);
      }
    }

    private async Task<long> _sealAsync() {
      await using var conn = await _openAsync(Consumer);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT sealed_through FROM wh_integrity_seals WHERE origin_service_id = @o";
      cmd.Parameters.AddWithValue("o", OriginId);
      var v = await cmd.ExecuteScalarAsync();
      return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>An operator's redelivery request for whole streams.</summary>
    public async Task OperatorRedeliverAsync(IReadOnlyList<Guid> streams, string[] types) {
      await _originRedeliveryReceptor.HandleAsync(new RequestRedeliveryCommand {
        RequesterService = CONSUMER_NAME,
        Topic = CONSUMER_TOPIC,
        StreamIds = streams,
        EventTypes = types,
        OperatorRequested = true,
      });
      await _pumpUntilQuietAsync();
    }

    private async Task _pumpUntilQuietAsync() {
      for (var guard = 0; guard < 200; guard++) {
        var progressed = false;
        while (_processedOrigin < _originTransport.Published.Count) {
          var (envelope, _, envelopeType) = _originTransport.Published[_processedOrigin++];
          progressed = true;
          if (envelopeType?.Contains("RedeliveryComposite", StringComparison.Ordinal) == true) {
            await _applyBundleAsync(envelope);
          } else if (envelopeType?.Contains("IntegrityManifest", StringComparison.Ordinal) == true) {
            await _consumerManifestReceptor.HandleAsync(_payload<IntegrityManifest>(envelope));
          }
        }
        while (_processedConsumer < _consumerTransport.Published.Count) {
          var (envelope, _, envelopeType) = _consumerTransport.Published[_processedConsumer++];
          progressed = true;
          if (envelopeType?.Contains("RequestIntegrityManifest", StringComparison.Ordinal) == true) {
            await _originRequestReceptor.HandleAsync(_payload<RequestIntegrityManifest>(envelope));
          } else if (envelopeType?.Contains("RequestRedeliveryCommand", StringComparison.Ordinal) == true) {
            await _originRedeliveryReceptor.HandleAsync(_payload<RequestRedeliveryCommand>(envelope));
          }
        }
        if (!progressed) {
          return;
        }
      }
      throw new InvalidOperationException("the wire did not quiesce: the protocol is not converging");
    }

    /// <summary>A redelivery bundle reaches the consumer: expanded by the real composite fan-out, stored by the inbox.</summary>
    private async Task _applyBundleAsync(IMessageEnvelope envelope) {
      var bundle = _payload<RedeliveryComposite>(envelope);
      _redelivered.AddRange(bundle.InnerEventIds);
      using var scope = _consumerProvider!.CreateScope();
      var result = CompositeInboxFanout.TryExpand(bundle, envelope, scope.ServiceProvider);
      if (result.Outcome != CompositeInboxFanout.FanoutOutcome.Expanded) {
        throw new InvalidOperationException($"the redelivery bundle did not expand: {result.Outcome} {result.Detail}");
      }
      // As the inbox worker does once the children are committed (#1790, #2033): the bundle's answer is recorded and
      // the redelivery reconciled, which marks a bucket the bundle could not change as one repair cannot heal.
      var children = result.Children.Where(c => c.SourceServiceId == bundle.OriginServiceId).ToList();
      _restored.AddRange(await _storeOnConsumerAsync([.. result.Children], async () =>
        _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, scope.ServiceProvider,
          NullLogger.Instance, _clock, RedeliveryReconciler.OriginServiceNameOf(envelope))));
    }

    private T _payload<T>(IMessageEnvelope envelope) =>
      (T)JsonSerializer.Deserialize(((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(), _json.GetTypeInfo(typeof(T)))!;

    /// <summary>What the wire has carried so far, split into the control stream and everything else.</summary>
    public CycleTally Tally(Guid control) {
      var controlStream = _emitted[control].Stream;
      var repairs = 0;
      var controlRepairs = 0;
      foreach (var (envelope, _, type) in _consumerTransport.Published) {
        if (type?.Contains("RequestRedeliveryCommand", StringComparison.Ordinal) != true) {
          continue;
        }
        var cmd = _payload<RequestRedeliveryCommand>(envelope);
        var streams = cmd.StreamIds ?? [];
        controlRepairs += streams.Count(s => s == controlStream);
        repairs += streams.Count(s => s != controlStream) + (streams.Count == 0 ? 1 : 0);
      }
      var reports = _consumerDispatcher.Published.OfType<IntegrityDivergenceDetected>()
        .Count(r => r.AuditedStreamId != controlStream && r.AuditedStreamId != Guid.Empty);
      bool outside(Guid id) => !_emitted.TryGetValue(id, out var e) || e.Stream != controlStream;
      return new CycleTally(repairs, reports, _redelivered.Count(outside), _restored.Count(outside), controlRepairs);
    }

    public async Task<bool> ConsumerHoldsAsync(Guid eventId) {
      await using var conn = await _openAsync(Consumer);
      return await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id", ("id", eventId)) == 1;
    }

    /// <summary>
    /// Turns the cut gate on in both stores, pushes the history past an epoch boundary with a filler stream delivered
    /// live, closes the epochs on both sides, and runs one clean windowed audit so the consumer's seal covers it.
    /// </summary>
    public async Task SealAndVerifyAsync() {
      foreach (var store in new[] { Origin, Consumer }) {
        await SetSettingAsync(store, "integrity_cut_waits_for_seal", "true", "boolean");
      }
      var filler = (Guid)TrackedGuid.New();
      var ids = await EmitAsync([.. Enumerable.Range(0, 6).Select(_ => new Emit(filler, FILLER, 0, null))]);
      await DeliverAsync(ids);
      await CloseEpochsAsync(Origin);
      await CloseEpochsAsync(Consumer);
      await AuditAsync(IntegrityMechanism.AuditTypesWindowed);
    }

    // ----- the per-store mechanisms -----

    public async Task<DigestVerificationResult> VerifyDigestTableAsync(Store store) {
      await _ageDigestsAsync(store);
      await using var ctx = new WorkCoordinationDbContext(store.Options);
      return await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json).VerifyDigestTableAsync(TimeSpan.Zero);
    }

    public static async Task CloseEpochsAsync(Store store) {
      await _ageDigestsAsync(store);
      await using var conn = await _openAsync(store);
      await _setSettingAsync(conn, "integrity_epoch_width", "3", "integer");
      await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
    }

    public static async Task<(int Checked, int Drifted)> VerifyEpochsAsync(Store store) {
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT epochs_checked, epochs_drifted FROM verify_digest_epochs(NOW(), 0, 1000)";
      await using var reader = await cmd.ExecuteReaderAsync();
      await reader.ReadAsync();
      return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public static async Task CorruptOneEpochAsync(Store store, string eventType) {
      await using var conn = await _openAsync(store);
      var changed = await _scalarAsync(conn, """
        WITH one AS (
          SELECT origin_service_id, scope_tenant, event_type, epoch_id FROM wh_digest_epochs
          WHERE event_type = @t ORDER BY epoch_id LIMIT 1)
        , upd AS (
          UPDATE wh_digest_epochs d SET digest_lo = d.digest_lo # 1
          FROM one WHERE d.origin_service_id = one.origin_service_id AND d.scope_tenant = one.scope_tenant
            AND d.event_type = one.event_type AND d.epoch_id = one.epoch_id
          RETURNING 1)
        SELECT COUNT(*) FROM upd
        """, ("t", eventType));
      if (changed != 1) {
        throw new InvalidOperationException("the positive control found no sealed epoch to damage: the setup is wrong");
      }
    }

    /// <summary>Removes one stored event of the type from the store without touching its digest: an unaccounted loss.</summary>
    public static async Task LoseOneEventUnaccountedAsync(Store store, string eventType) {
      await using var conn = await _openAsync(store);
      var id = await _scalarGuidAsync(conn,
        "SELECT event_id FROM wh_event_store WHERE event_type = @t ORDER BY event_id LIMIT 1", ("t", eventType));
      await _executeAsync(conn, "DELETE FROM wh_event_body WHERE event_id = @id", ("id", id));
      await _executeAsync(conn, "DELETE FROM wh_event_store WHERE event_id = @id", ("id", id));
    }

    // ----- the invariant -----

    /// <summary>
    /// On each side, every stored digest of the scenario equals a fresh fold over the events still ELIGIBLE to count,
    /// plus what cuts carried: durable (not ephemeral, not at most once), not digest-exempt, and, for a superseding type,
    /// each key once, as its slot, whichever of its copies are held (#2364). A removal that left its contribution behind
    /// uncarried, or a key counted per copy, fails here.
    /// </summary>
    public async Task AssertDigestsFollowEligibilityAsync(MatrixCase pair) {
      foreach (var store in new[] { Origin, Consumer }) {
        await using var conn = await _openAsync(store);
        var differing = await _scalarAsync(conn, """
          WITH eligible AS (
            -- #2364: a superseding key counts once, as its slot (stream, group, key), whichever of its copies are held,
            -- in its first copy's type bucket; the copy that counts it is the oldest held.
            SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
                   COALESCE(es.scope->>'t', '') AS scope_tenant,
                   CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS event_type,
                   es.stream_id,
                   bit_xor(hashtextextended(x.id::text, 0)) AS digest_lo,
                   bit_xor(hashtextextended(x.id::text, 1)) AS digest_hi,
                   COUNT(*)::int AS event_count
            FROM wh_event_store es
            CROSS JOIN LATERAL (SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id
                                            ELSE md5(es.stream_id::text || '|' || COALESCE(es.supersede_group, es.event_type)
                                                     || '|' || es.supersede_key)::uuid END AS id) x
            WHERE COALESCE(es.flags, 0) & 8 = 0
              AND COALESCE(es.flags, 0) & 32 = 0
              AND split_part(es.event_type, ',', 1) <> ALL(@exempt::text[])
              AND NOT (es.supersede_key IS NOT NULL AND EXISTS (
                SELECT 1 FROM wh_event_store o
                WHERE o.stream_id = es.stream_id
                  AND COALESCE(o.supersede_group, o.event_type) = COALESCE(es.supersede_group, es.event_type)
                  AND o.supersede_key = es.supersede_key
                  AND o.event_id <> es.event_id
                  AND CASE WHEN o.origin_service_id IS NOT NULL AND o.origin_service_id = es.origin_service_id
                                AND o.origin_commit_sequence IS NOT NULL AND es.origin_commit_sequence IS NOT NULL
                           THEN o.origin_commit_sequence < es.origin_commit_sequence
                           ELSE o.version < es.version END))
            GROUP BY 1, 2, 3, 4
          ),
          -- #2363: a bucket's digest is its live events plus what cuts carried; the live part is what must equal the fold.
          stored AS (
            SELECT origin_service_id, scope_tenant, event_type, stream_id,
                   digest_lo # carried_lo AS digest_lo, digest_hi # carried_hi AS digest_hi,
                   event_count - carried_count AS event_count
            FROM wh_stream_digests
            WHERE carried_lo IS NOT NULL
              AND NOT (event_count - carried_count = 0 AND digest_lo # carried_lo = 0 AND digest_hi # carried_hi = 0))
          SELECT COUNT(*) FROM ((TABLE eligible EXCEPT TABLE stored) UNION ALL (TABLE stored EXCEPT TABLE eligible)) d
          """, ("exempt", Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames.ToArray()));
        await Assert.That(differing).IsEqualTo(0L)
          .Because($"[{pair}] on the {store.Name}, every stored digest must equal a fold over the events still eligible "
                 + "to count; a different one is either a removal that left its contribution behind or a copy counted "
                 + "that its rule says no longer counts");
      }
    }

    // ----- small reads -----

    /// <summary>What a store holds of the streams: each event's id and flags, and whether its body is still there.</summary>
    public static async Task<string> FingerprintAsync(Store store, IReadOnlyList<Guid> streams) {
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        SELECT COALESCE(string_agg(es.event_id || ':' || COALESCE(es.flags, 0) || ':'
                 || (EXISTS (SELECT 1 FROM wh_event_body b WHERE b.event_id = es.event_id))::text, ',' ORDER BY es.event_id), '')
        FROM wh_event_store es WHERE es.stream_id = ANY(@s)
        """;
      cmd.Parameters.AddWithValue("s", streams.ToArray());
      return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<List<string>> HeldEventsAsync(Store store, IReadOnlyList<Guid> streams) {
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT event_id::text FROM wh_event_store WHERE stream_id = ANY(@s) ORDER BY event_id";
      cmd.Parameters.AddWithValue("s", streams.ToArray());
      var rows = new List<string>();
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add(reader.GetString(0));
      }
      return rows;
    }

    public static async Task<List<string>> DigestRowsAsync(Store store, IReadOnlyList<Guid> streams) {
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        SELECT stream_id || '|' || event_type || '|' || digest_lo || '|' || digest_hi || '|' || event_count
        FROM wh_stream_digests WHERE stream_id = ANY(@s) ORDER BY 1
        """;
      cmd.Parameters.AddWithValue("s", streams.ToArray());
      var rows = new List<string>();
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add(reader.GetString(0));
      }
      return rows;
    }

    /// <summary>The version the nth stored event of a stream (by event id) holds on this store, or null when it is gone.</summary>
    public async Task<long?> VersionOfNthAsync(Store store, Guid stream, int n) {
      var ids = _emitted.Where(e => e.Value.Stream == stream).Select(e => e.Key).Order().ToList();
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT version FROM wh_event_store WHERE event_id = @id";
      cmd.Parameters.AddWithValue("id", ids[n - 1]);
      var v = await cmd.ExecuteScalarAsync();
      return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    public async Task ArchiveAsync(Store store, Guid stream, long? throughVersion) {
      await using var conn = await _openAsync(store);
      var archived = await _scalarAsync(conn,
        "SELECT COUNT(*) FROM wh_stream_lifecycle WHERE stream_id = @s AND state = 'Archived'", ("s", stream));
      if (archived > 0) {
        return;
      }
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT * FROM wh_archive_stream(@s, @v, @l)";
      cmd.Parameters.AddWithValue("s", stream);
      cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Bigint) { Value = (object?)throughVersion ?? DBNull.Value });
      cmd.Parameters.AddWithValue("l", "matrix://" + stream);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // Drained: the call is run for its effect, and every result row is read so a failure in it surfaces.
      }
    }

    public static async Task SetSettingAsync(Store store, string key, string value, string type) {
      await using var conn = await _openAsync(store);
      await _setSettingAsync(conn, key, value, type);
    }

    private static Task _setSettingAsync(NpgsqlConnection conn, string key, string value, string type) =>
      _executeAsync(conn, """
        INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
        VALUES (@k, @v, @ty, 'matrix')
        ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
        """, ("k", key), ("v", value), ("ty", type));

    public static async Task ExecuteAsync(Store store, string sql, params (string Name, object Value)[] parameters) {
      await using var conn = await _openAsync(store);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      foreach (var (name, value) in parameters) {
        cmd.Parameters.AddWithValue(name, value);
      }
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // Drained: the call is run for its effect, and every result row is read so a failure in it surfaces.
      }
    }

    /// <summary>Moves every bucket out of the settle window on the database clock, so the heal and the epoch closure
    /// judge them.</summary>
    private static async Task _ageDigestsAsync(Store store) {
      await using var conn = await _openAsync(store);
      await _executeAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    }

    private async Task<Guid> _localServiceIdAsync(Store store) {
      await using var ctx = new WorkCoordinationDbContext(store.Options);
      return await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json).GetLocalServiceIdAsync();
    }

    private ServiceProvider _buildProvider(
        Store store, IntegrityWireTransport transport, IntegrityWireDispatcher dispatcher, string serviceName,
        StreamIntegrityOptions options, IntegrityGapTracker? tracker) {
      var services = new ServiceCollection();
      services.AddScoped<IWorkCoordinator>(_ =>
        new EFCoreWorkCoordinator<WorkCoordinationDbContext>(new WorkCoordinationDbContext(store.Options), _json));
      services.AddSingleton<ITransport>(transport);
      services.AddSingleton<IDispatcher>(dispatcher);
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(_json));
      services.AddSingleton<IServiceInstanceProvider>(new IntegrityWireInstance(serviceName));
      services.AddSingleton(Options.Create(options));
      // The durable, coordinator-backed ledger the turnkey registration uses: it holds the unhealable marks the
      // redelivery reconciliation writes, so the compare sees them.
      services.AddSingleton<IIntegrityRepairLedger>(sp =>
        new CoordinatorIntegrityRepairLedger(sp.GetRequiredService<IServiceScopeFactory>()));
      services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
      // The consumer's catalog: what the fan-out derives a redelivered child's flags from, by its type name.
      services.AddSingleton<IEventMarkerResolver>(new MatrixMarkers());
      if (tracker is not null) {
        services.AddSingleton(tracker);
      }
      var consumerOptions = new TransportConsumerOptions();
      consumerOptions.Destinations.Add(new TransportDestination(CONSUMER_TOPIC));
      services.AddSingleton(consumerOptions);
      return services.BuildServiceProvider();
    }

    private static async Task<NpgsqlConnection> _openAsync(Store store) {
      var conn = new NpgsqlConnection(store.ConnectionString);
      await conn.OpenAsync();
      return conn;
    }

    private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      foreach (var (name, value) in parameters) {
        cmd.Parameters.AddWithValue(name, value);
      }
      _ = await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      foreach (var (name, value) in parameters) {
        cmd.Parameters.AddWithValue(name, value);
      }
      return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> _scalarGuidAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      foreach (var (name, value) in parameters) {
        cmd.Parameters.AddWithValue(name, value);
      }
      return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
  }

  /// <summary>A fake clock that releases <see cref="Armed"/> each time a timer is created, which is each time the drain's
  /// loop starts waiting out its interval.</summary>
  private sealed class ArmSignalingClock(DateTimeOffset start) : TimeProvider {
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _inner = new(start);
    public System.Threading.SemaphoreSlim Armed { get; } = new(0);
    public void Advance(TimeSpan by) => _inner.Advance(by);
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
    public override System.Threading.ITimer CreateTimer(System.Threading.TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = _inner.CreateTimer(callback, state, dueTime, period);
      Armed.Release();
      return timer;
    }
  }

  /// <summary>The flags the matrix's types declare, by CLR name, as a generated catalog would answer.</summary>
  private sealed class MatrixMarkers : IEventMarkerResolver {
    public EventFlags? Resolve(string clrTypeName) => clrTypeName switch {
      _ when clrTypeName == typeof(SupersedingProbeEmbedded).FullName => EventFlags.Superseding,
      "Matrix.Contracts.CursorMoved" => EventFlags.Ephemeral,
      _ => null,
    };

    public EventFlags? Resolve(Type type) => null;
  }
}
