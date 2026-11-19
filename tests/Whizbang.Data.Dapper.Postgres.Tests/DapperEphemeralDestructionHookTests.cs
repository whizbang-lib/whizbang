// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The ephemeral reap honors a registered <see cref="IDestructionHook"/> on the Dapper driver, with the contract the EF
/// Core driver keeps: the hook is offered exactly the bodies the reap then deletes, its Cancel holds them, a body that
/// becomes eligible while it runs waits for the next cycle, and a throwing hook holds the batch for retry (#2039).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's defaults for the reap's destruction seam: nothing about to be reaped, holds
/// that recorded nothing, a failure count that forced the delete at once, and a restricted reap that deleted every
/// eligible body. A Dapper host with a hook registered for compliance or export saw nothing while the bodies went.
/// </para>
/// <para>
/// Each test has a twin on the EF Core driver (<c>EphemeralDestructionTargetSqlTests</c>,
/// <c>EphemeralDestructionHoldSqlTests</c>, <c>EphemeralReapSeenSetTests</c>). Design choice documented at
/// fundamentals/events/ephemeral-events#destruction-hooks-on-every-data-layer.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#destruction-hooks-on-every-data-layer</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.EphemeralDestruction.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs</code-under-test>
[Category("Integration")]
public class DapperEphemeralDestructionHookTests : PostgresTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.DapperReapEvent";

  /// <summary>Records what it is offered and announced, and answers with <see cref="Decide"/>.</summary>
  private sealed class RecordingHook : IDestructionHook {
    public Func<DestructionContext, CancellationToken, ValueTask<DestructionResult>> Decide { get; set; } =
      (_, _) => ValueTask.FromResult(DestructionResult.Proceed());

    public List<IReadOnlyList<Guid>> Offered { get; } = [];
    public List<IReadOnlyList<Guid>> Announced { get; } = [];

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Offered.Add([.. context.Targets.Select(t => t.EventId)]);
      return Decide(context, cancellationToken);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Announced.Add([.. context.Targets.Select(t => t.EventId)]);
      return ValueTask.CompletedTask;
    }
  }

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private MaintenanceWorker _worker(IDestructionHook hook, int maxRetries = 5) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => _coordinator());
    services.AddSingleton(hook);
    var provider = services.BuildServiceProvider();
    return new MaintenanceWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, MaxDestructionRetries = maxRetries }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task AConsumedAgedBody_IsOfferedToTheHook_ThenReaped_AndAnnouncedAsync() {
    var aged = await _commitEphemeralAsync(agePastGrace: true);
    var recent = await _commitEphemeralAsync(agePastGrace: false);
    var hook = new RecordingHook();

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(_worker(hook));

    await Assert.That(hook.Offered).Count().IsEqualTo(1);
    await Assert.That(hook.Offered[0]).IsEquivalentTo([aged])
      .Because("the hook is offered exactly the body the reap deletes; a body still inside its grace window is not one");
    await Assert.That(await _bodyCountAsync(aged)).IsEqualTo(0L);
    await Assert.That(await _bodyCountAsync(recent)).IsEqualTo(1L);
    await Assert.That(hook.Announced).Count().IsEqualTo(1);
    await Assert.That(hook.Announced[0]).IsEquivalentTo([aged]);
  }

  [Test]
  public async Task AHookThatCancels_KeepsTheBody_AndIsNotOfferedItAgainWhileTheHoldStandsAsync() {
    var aged = await _commitEphemeralAsync(agePastGrace: true);
    var hook = new RecordingHook { Decide = (_, _) => ValueTask.FromResult(DestructionResult.Canceled) };
    var worker = _worker(hook);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(await _bodyCountAsync(aged)).IsEqualTo(1L)
      .Because("Cancel holds the body, and the reap honors the hold");
    await Assert.That(hook.Announced).IsEmpty();

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(hook.Offered).Count().IsEqualTo(1)
      .Because("a held body is not offered again until its hold lapses");
    await Assert.That(await _bodyCountAsync(aged)).IsEqualTo(1L);
  }

  [Test]
  public async Task ABodyThatBecomesEligibleWhileTheHookRuns_SurvivesTheCycle_AndIsOfferedOnTheNextAsync() {
    var first = await _commitEphemeralAsync(agePastGrace: true);
    var late = await _commitEphemeralAsync(agePastGrace: false);
    var hook = new RecordingHook();
    var agedLate = false;
    hook.Decide = async (_, cancellationToken) => {
      if (!agedLate) {
        agedLate = true;
        await _agePastGraceAsync(late, cancellationToken);
      }
      return DestructionResult.Proceed();
    };
    var worker = _worker(hook);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(await _bodyCountAsync(first)).IsEqualTo(0L);
    await Assert.That(await _bodyCountAsync(late)).IsEqualTo(1L)
      .Because("the reap deletes only what the hook was offered; the late body was not");

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(hook.Offered).Count().IsEqualTo(2);
    await Assert.That(hook.Offered[1]).IsEquivalentTo([late]);
    await Assert.That(await _bodyCountAsync(late)).IsEqualTo(0L);
  }

  [Test]
  public async Task AThrowingHook_HoldsTheBatchForRetry_ThenForcesTheDeletePastTheCapAsync() {
    var aged = await _commitEphemeralAsync(agePastGrace: true);
    var hook = new RecordingHook { Decide = (_, _) => throw new InvalidOperationException("export target unavailable") };
    var worker = _worker(hook, maxRetries: 1);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(await _bodyCountAsync(aged)).IsEqualTo(1L)
      .Because("a failing hook holds its batch for a retry instead of letting the reap go ahead");
    await Assert.That(await _scalarAsync<int>("SELECT failure_count FROM wh_event_destruction_hold WHERE event_id = @id", new { id = aged }))
      .IsEqualTo(1);

    // The retry backoff lapses: the batch is offered again, fails a second time, passes the cap of one, and is forced.
    await _executeAsync("UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE event_id = @id", new { id = aged });
    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(hook.Offered).Count().IsEqualTo(2);
    await Assert.That(await _bodyCountAsync(aged)).IsEqualTo(0L)
      .Because("past the retry cap the default policy forces the delete, so a broken hook cannot keep storage forever");
  }

  [Test]
  public async Task EmptyBatches_OpenNothing_AndCountNoFailureAsync() {
    // Through the interface on purpose: before #2039 these members were the interface's defaults, which the class did
    // not declare, so only this spelling reaches whatever the coordinator answers.
#pragma warning disable CA1859
    IWorkCoordinator coordinator = new DapperWorkCoordinator(
      "Host=unreachable.invalid;Database=none", Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
#pragma warning restore CA1859

    await coordinator.HoldEphemeralDestructionAsync([], DateTimeOffset.MaxValue);
    await Assert.That(await coordinator.RecordDestructionFailureAsync([], DateTimeOffset.MaxValue, 5)).IsEqualTo(0)
      .Because("an empty batch returns before a connection is opened, to a server that does not exist");
  }

  private async Task<Guid> _commitEphemeralAsync(bool agePastGrace) {
    var eventId = (Guid)TrackedGuid.New();
    var request = $$"""
      {
        "instance_id": "{{(Guid)TrackedGuid.New()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EVENT_TYPE}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{(Guid)TrackedGuid.New()}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await _executeAsync("SELECT commit_handler_result(@req::jsonb)", new { req = request });
    if (agePastGrace) {
      await _agePastGraceAsync(eventId, CancellationToken.None);
    }
    return eventId;
  }

  // Aged on the database clock, which is the clock the grace window is compared against.
  private async Task _agePastGraceAsync(Guid eventId, CancellationToken cancellationToken) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id",
      new { id = eventId }, cancellationToken: cancellationToken));
  }

  private Task<long> _bodyCountAsync(Guid eventId) =>
    _scalarAsync<long>("SELECT count(*) FROM wh_event_body WHERE event_id = @id", new { id = eventId });

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task _executeAsync(string sql, object parameters) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object parameters) {
    await using var connection = await _openAsync();
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }
}
