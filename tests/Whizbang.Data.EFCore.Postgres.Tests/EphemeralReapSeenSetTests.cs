// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1999: the ephemeral reaper deletes exactly the bodies its PreDestruction hook and compaction handler were given. The
/// maintenance worker reads the bodies about to be reaped, runs the hook (and, for <c>Compact</c>, the handler) over them,
/// and then the reap runs as a separate statement. A body that became eligible in between (here: its grace window
/// passes while the hook runs) used to be reaped by that statement without the hook or the handler ever seeing it. Each
/// test makes the second body eligible from inside the hook or the handler, so the gap is opened deterministically, and
/// checks the body survives the cycle and is offered, then reaped, on the next.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>fundamentals/events/ephemeral-events#reap-deletes-what-the-hook-saw</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class EphemeralReapSeenSetTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.SeenSetEvent";

  /// <summary>Ages one event past the grace window: the deterministic stand-in for time passing during the hook.</summary>
  private sealed class Ager(string connectionString) {
    public Guid? Pending { get; set; }

    public async ValueTask AgeAsync(CancellationToken cancellationToken) {
      if (Pending is not { } id) {
        return;
      }
      Pending = null;
      await using var conn = new NpgsqlConnection(connectionString);
      await conn.OpenAsync(cancellationToken);
      await _agePastGraceAsync(conn, id, cancellationToken);
    }
  }

  private sealed class RecordingHook(Ager ager, Disposition? disposition) : IDestructionHook {
    public List<IReadOnlyList<Guid>> Offered { get; } = [];

    public async ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Offered.Add([.. context.Targets.Select(t => t.EventId)]);
      if (disposition is null) {
        await ager.AgeAsync(cancellationToken);
      }
      return DestructionResult.Proceed(disposition);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class AgingCompactor(Ager ager) : ICompactionHandler {
    public List<IReadOnlyList<Guid>> Compacted { get; } = [];

    public async ValueTask CompactAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Compacted.Add([.. context.Targets.Select(t => t.EventId)]);
      await ager.AgeAsync(cancellationToken);
    }
  }

  private MaintenanceWorker _worker(IDestructionHook hook, ICompactionHandler? compactor) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(hook);
    if (compactor is not null) {
      services.AddSingleton(compactor);
    }
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  private static async Task _commitEphemeralAsync(NpgsqlConnection conn, Guid eventId, CancellationToken cancellationToken) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EVENT_TYPE}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{Guid.NewGuid()}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await using var cmd = new NpgsqlCommand("SELECT commit_handler_result(@req::jsonb)", conn);
    cmd.Parameters.AddWithValue("req", request);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  // Ten minutes against the 300-second default grace window: past it by a margin no clock skew reaches.
  private static async Task _agePastGraceAsync(NpgsqlConnection conn, Guid eventId, CancellationToken cancellationToken) {
    await using var cmd = new NpgsqlCommand(
      "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<bool> _hasBodyAsync(NpgsqlConnection conn, Guid eventId, CancellationToken cancellationToken) {
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_event_body WHERE event_id = @id", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    return (long)(await cmd.ExecuteScalarAsync(cancellationToken))! > 0;
  }

  /// <summary>Seeds the body the hook is offered (aged) and the one that becomes eligible during the hook (fresh).</summary>
  private async Task<(NpgsqlConnection Conn, Guid Seen, Guid Late, Ager Ager)> _seedAsync(CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var seen = Guid.NewGuid();
    var late = Guid.NewGuid();
    await _commitEphemeralAsync(conn, seen, cancellationToken);
    await _commitEphemeralAsync(conn, late, cancellationToken);
    await _agePastGraceAsync(conn, seen, cancellationToken);
    return (conn, seen, late, new Ager(ConnectionString) { Pending = late });
  }

  [Test]
  [Timeout(120000)]
  public async Task Delete_ABodyThatBecomesEligibleWhileTheHookRuns_IsNotReapedUnseen_AndIsOfferedNextCycleAsync(CancellationToken cancellationToken) {
    var (conn, seen, late, ager) = await _seedAsync(cancellationToken);
    await using var _ = conn;
    var hook = new RecordingHook(ager, disposition: null);
    var worker = _worker(hook, compactor: null);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(hook.Offered).Count().IsEqualTo(1);
    await Assert.That(hook.Offered[0]).IsEquivalentTo([seen])
      .Because("the late body was still inside its grace window when the reaper read the batch");
    await Assert.That(await _hasBodyAsync(conn, seen, cancellationToken)).IsFalse()
      .Because("the control: the body the hook proceeded with is reaped in the same cycle");
    await Assert.That(await _hasBodyAsync(conn, late, cancellationToken)).IsTrue()
      .Because("the hook never saw the late body, so its Cancel, Defer or hold could not apply to it; reaping it would bypass the hook");

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(hook.Offered).Count().IsEqualTo(2);
    await Assert.That(hook.Offered[1]).IsEquivalentTo([late])
      .Because("the next cycle offers the body the previous one held back");
    await Assert.That(await _hasBodyAsync(conn, late, cancellationToken)).IsFalse()
      .Because("once the hook has seen it and proceeded, it is reaped");
  }

  [Test]
  [Timeout(120000)]
  public async Task Compact_ABodyThatBecomesEligibleWhileTheHandlerRuns_IsNotReapedUncompacted_AndIsCompactedNextCycleAsync(CancellationToken cancellationToken) {
    var (conn, seen, late, ager) = await _seedAsync(cancellationToken);
    await using var _ = conn;
    var hook = new RecordingHook(ager, Disposition.Compact);
    var compactor = new AgingCompactor(ager);
    var worker = _worker(hook, compactor);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(compactor.Compacted).Count().IsEqualTo(1);
    await Assert.That(compactor.Compacted[0]).IsEquivalentTo([seen]);
    await Assert.That(await _hasBodyAsync(conn, seen, cancellationToken)).IsFalse()
      .Because("the control: the compacted body is reaped in the same cycle");
    await Assert.That(await _hasBodyAsync(conn, late, cancellationToken)).IsTrue()
      .Because("the handler never folded the late body; discarding it would lose detail no summary holds");

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(compactor.Compacted).Count().IsEqualTo(2);
    await Assert.That(compactor.Compacted[1]).IsEquivalentTo([late]);
    await Assert.That(await _hasBodyAsync(conn, late, cancellationToken)).IsFalse();
  }
  [Test]
  [Timeout(120000)]
  public async Task Reap_GivenIds_DeletesOnlyThose_AnEmptyListNothing_AndNullEveryEligibleBodyAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var given = Guid.NewGuid();
    var givenButInGrace = Guid.NewGuid();
    var other = Guid.NewGuid();
    foreach (var id in new[] { given, givenButInGrace, other }) {
      await _commitEphemeralAsync(conn, id, cancellationToken);
    }
    await _agePastGraceAsync(conn, given, cancellationToken);
    await _agePastGraceAsync(conn, other, cancellationToken);
    await using var ctx = CreateDbContext();
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await coordinator.PerformMaintenanceAsync([given, givenButInGrace], cancellationToken);

    await Assert.That(await _hasBodyAsync(conn, given, cancellationToken)).IsFalse();
    await Assert.That(await _hasBodyAsync(conn, givenButInGrace, cancellationToken)).IsTrue()
      .Because("the ids restrict the reap; they never lift a gate, so a given body still inside its grace window stays");
    await Assert.That(await _hasBodyAsync(conn, other, cancellationToken)).IsTrue()
      .Because("an eligible body that was not given is left for a later cycle");

    var reaped = await coordinator.PerformMaintenanceAsync([], cancellationToken);

    await Assert.That(reaped.Single(r => r.TaskName == "reap_consumed_ephemeral_bodies").RowsAffected).IsEqualTo(0L)
      .Because("an empty batch means the hook saw nothing, so nothing may go");
    await Assert.That(await _hasBodyAsync(conn, other, cancellationToken)).IsTrue();

    await coordinator.PerformMaintenanceAsync(null, cancellationToken);

    await Assert.That(await _hasBodyAsync(conn, other, cancellationToken)).IsFalse()
      .Because("the control: with no restriction every eligible body goes, as perform_maintenance() always did");
    await Assert.That(await _hasBodyAsync(conn, givenButInGrace, cancellationToken)).IsTrue();
  }
}
