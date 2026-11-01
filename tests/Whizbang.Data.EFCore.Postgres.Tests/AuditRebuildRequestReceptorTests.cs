// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1724 (WS-2 S7), end to end on a real event store: <see cref="AuditRebuildRequestReceptor"/> selects the stored
/// events, keeps the types audit eligibility admits, and queues one record per origin event, under an id that a second
/// rebuild derives again.
/// </summary>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/AuditRebuildRequestReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditLedgerRebuild.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RedeliverySelection")]
[Category("Shard4")]
public class AuditRebuildRequestReceptorTests : EFCoreTestBase {
  private const string TENANT = "tenant-a";

  internal sealed record LedgerOrderPlaced : IEvent;

  [AuditEvent(Exclude = true, Reason = "heartbeat")]
  internal sealed record LedgerHeartbeat : IEvent;

  internal sealed record LedgerPresence : IEphemeralEvent;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(LedgerOrderPlaced), TypeNameFormatter.FormatClrTypeName(typeof(LedgerOrderPlaced)), "event", null),
      new(typeof(LedgerHeartbeat), TypeNameFormatter.FormatClrTypeName(typeof(LedgerHeartbeat)), "event", null),
      new(typeof(LedgerPresence), TypeNameFormatter.FormatClrTypeName(typeof(LedgerPresence)), "event", null),
    ];
  }

  private sealed class RecordingChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> Queued { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      Queued.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() => [.. Queued];

    public bool HasPending => Queued.Count > 0;
  }

  private ServiceProvider _provider(RecordingChannel channel, bool auditEnabled = true, bool full = true) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IDeferredOutboxChannel>(channel);
    if (full) {
      var options = new SystemEventOptions();
      if (auditEnabled) {
        options.EnableEventAudit();
      }
      services.AddSingleton(Options.Create(options));
      services.AddSingleton<IMessageTypeCatalog>(new Catalog());
      services.AddSingleton(new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 });
    }
    return services.BuildServiceProvider();
  }

  private async Task<Guid> _seedAsync(Guid streamId, int version, Type type, int flags = 0) {
    var eventId = TrackedGuid.New().Value;
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var command = conn.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), @flags);
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@event, '{"seeded":true}'::jsonb, @meta::jsonb);
      """;
    command.Parameters.AddWithValue("event", eventId);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("type", TypeNameFormatter.Format(type));
    command.Parameters.AddWithValue("scope", $$"""{"t":"{{TENANT}}"}""");
    command.Parameters.AddWithValue(nameof(version), version);
    command.Parameters.AddWithValue(nameof(flags), flags);
    command.Parameters.AddWithValue("meta", $$"""{"MessageId":"{{eventId}}","Hops":[{"ts":"2026-03-04T05:06:07+00:00"}]}""");
    await command.ExecuteNonQueryAsync();
    return eventId;
  }

  [Test]
  public async Task Rebuild_FromTheStore_QueuesARecordForEachEligibleEventOnlyAsync() {
    var stream = TrackedGuid.New().Value;
    var placed = await _seedAsync(stream, 1, typeof(LedgerOrderPlaced));
    await _seedAsync(stream, 2, typeof(LedgerHeartbeat));
    await _seedAsync(stream, 3, typeof(LedgerPresence), flags: (int)EventFlags.Ephemeral);
    var placedAgain = await _seedAsync(stream, 4, typeof(LedgerOrderPlaced));
    var channel = new RecordingChannel();
    await using var provider = _provider(channel);
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestAuditRebuildCommand { TenantScope = TENANT, StreamIds = [stream] });

    var audited = channel.Queued.Select(m => m.Envelope.Payload.GetProperty("OriginalEventId").GetGuid()).ToList();
    await Assert.That(audited).IsEquivalentTo(new[] { placed, placedAgain })
      .Because("the heartbeat is excluded and the presence is ephemeral; only the two order events are audited");
    await Assert.That(channel.Queued[0].StreamId).IsEqualTo(AuditLedger.StreamFor(TENANT));
  }

  [Test]
  public async Task Rebuild_Twice_YieldsOneRecordPerOriginEventAsync() {
    var stream = TrackedGuid.New().Value;
    var first = await _seedAsync(stream, 1, typeof(LedgerOrderPlaced));
    var second = await _seedAsync(stream, 2, typeof(LedgerOrderPlaced));
    var third = await _seedAsync(stream, 3, typeof(LedgerOrderPlaced));
    var channel = new RecordingChannel();
    await using var provider = _provider(channel);
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestAuditRebuildCommand { StreamIds = [stream] });
    await receptor.HandleAsync(new RequestAuditRebuildCommand { StreamIds = [stream] });

    var recordIds = channel.Queued.Select(m => m.Envelope.Payload.GetProperty("Id").GetGuid()).Distinct().ToList();
    await Assert.That(channel.Queued).Count().IsEqualTo(6);
    await Assert.That(recordIds).IsEquivalentTo(new[] {
      AuditLedger.RecordIdFor(first), AuditLedger.RecordIdFor(second), AuditLedger.RecordIdFor(third) })
      .Because("every delivery of a record carries the id derived from its event, so the audit perspective holds one row each");
  }

  [Test]
  public async Task Rebuild_OnAServiceWithEventAuditOff_QueuesNothingAsync() {
    var stream = TrackedGuid.New().Value;
    await _seedAsync(stream, 1, typeof(LedgerOrderPlaced));
    var channel = new RecordingChannel();
    await using var provider = _provider(channel, auditEnabled: false);
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestAuditRebuildCommand { StreamIds = [stream] });

    await Assert.That(channel.Queued).IsEmpty();
  }

  [Test]
  public async Task Rebuild_WithOnlyTheCoordinatorAndChannel_UsesTheDefaultsAsync() {
    // No options registered: event audit is off by default, so nothing is rebuilt and nothing throws.
    var stream = TrackedGuid.New().Value;
    await _seedAsync(stream, 1, typeof(LedgerOrderPlaced));
    var channel = new RecordingChannel();
    await using var provider = _provider(channel, full: false);
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestAuditRebuildCommand { StreamIds = [stream] });

    await Assert.That(channel.Queued).IsEmpty();
  }

  [Test]
  [Arguments(true, false)]
  [Arguments(false, true)]
  public async Task Rebuild_WithoutTheCoordinatorOrTheChannel_DoesNothingAsync(bool withCoordinator, bool withChannel) {
    var services = new ServiceCollection();
    var channel = new RecordingChannel();
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    }
    if (withChannel) {
      services.AddSingleton<IDeferredOutboxChannel>(channel);
    }
    await using var provider = services.BuildServiceProvider();
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestAuditRebuildCommand());

    await Assert.That(channel.Queued).IsEmpty()
      .Because("a host missing either piece cannot rebuild; the request is logged and dropped, never thrown");
  }

  [Test]
  public async Task Rebuild_ANullCommand_ThrowsAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var receptor = new AuditRebuildRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditRebuildRequestReceptor>.Instance);

    await Assert.That(async () => await receptor.HandleAsync(null!)).Throws<ArgumentNullException>();
  }
}
