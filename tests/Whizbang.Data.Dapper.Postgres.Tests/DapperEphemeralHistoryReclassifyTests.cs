// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>An event type stored while it was Sourced, then declared ephemeral (the step's catalog says so).</summary>
public sealed record DapperEphemeralHistoryProbePinged : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>A second event type of the same session streams, declared ephemeral too (#2278).</summary>
public sealed record DapperEphemeralHistoryProbeCreated : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>
/// #2206: on the Dapper driver, as on EF Core, the history a type stored before it was declared [Ephemeral] is
/// reclassified by the registered maintenance step, not by startup. The event is written through this driver's own append
/// path, so <c>event_type</c> carries the stored form the walk has to name (#2254); the old fixture hand-typed the bare
/// name the step walked, which is how the mismatch passed.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
public class DapperEphemeralHistoryReclassifyTests : PostgresTestBase {
  [Test]
  public async Task HistoryStoredBeforeTheDeclaration_IsReclassifiedByTheRegisteredStepAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stream = (Guid)TrackedGuid.New();
    var type = typeof(DapperEphemeralHistoryProbePinged);
    await _writeSourcedAsync(stream, new DapperEphemeralHistoryProbePinged { SessionId = stream });
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    var id = await conn.ExecuteScalarAsync<Guid>("SELECT event_id FROM wh_event_store WHERE stream_id = @stream", new { stream });
    await Assert.That(await conn.ExecuteScalarAsync<string>("SELECT event_type FROM wh_event_store WHERE event_id = @id", new { id }))
      .IsEqualTo(TypeNameFormatter.Format(type)).Because("the control: the append path stores the stored form, not the catalog's bare name");
    await Assert.That(await conn.ExecuteScalarAsync<int>("SELECT flags & 8 FROM wh_event_store WHERE event_id = @id", new { id }))
      .IsEqualTo(0).Because("the control: the event was stored durable, before its type was declared ephemeral");
    await using var provider = _provider(_ephemeral(type));
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<EphemeralHistoryReclassifyStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);

    var flags = await conn.ExecuteScalarAsync<int>("SELECT flags FROM wh_event_store WHERE event_id = @id", new { id });
    await Assert.That(flags & 8).IsEqualTo(8).Because("the registered step reclassified the history in the maintenance cycle");
  }

  /// <summary>#2278: two declared-ephemeral types sharing a stream, both written durable through this driver's append
  /// path, are converted together by the first walk that reaches the stream.</summary>
  [Test]
  public async Task TwoDeclaredTypesSharingAStream_AreConvertedTogether_ByTheRegisteredStepAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stream = (Guid)TrackedGuid.New();
    await _writeSourcedAsync(stream, new DapperEphemeralHistoryProbeCreated { SessionId = stream });
    await _writeSourcedAsync(stream, new DapperEphemeralHistoryProbePinged { SessionId = stream });
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream AND (flags & 8) = 0", new { stream }))
      .IsEqualTo(2L).Because("the control: both events were stored durable, before their types were declared ephemeral");
    await using var provider = _provider(_ephemeral(typeof(DapperEphemeralHistoryProbePinged)), _ephemeral(typeof(DapperEphemeralHistoryProbeCreated)));
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<EphemeralHistoryReclassifyStep>().Single();

    var result = await step.ReclassifyAsync(scope.ServiceProvider, CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(0L).Because("neither declared type blocks the other");
    await Assert.That(result.EventsReclassified + result.EventsReclassifiedWithStream).IsEqualTo(2L)
      .Because("the walk converted both events, not the append path");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream AND (flags & 8) = 8", new { stream }))
      .IsEqualTo(2L).Because("the stream was converted whole");
  }

  // The events are written while their types are still Sourced: the catalog that declares them ephemeral also makes the
  // append path store them ephemeral, so writing through it would leave the walk nothing to do.
  private async Task _writeSourcedAsync<TEvent>(Guid stream, TEvent @event) where TEvent : notnull {
    await using var provider = _provider();
    await using var scope = provider.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(stream, @event);
  }

  private ServiceProvider _provider(params MessageTypeCatalogEntry[] declared) {
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    services.AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }));
    services.AddSingleton<IMessageTypeCatalog>(new Catalog(declared));
    return services.BuildServiceProvider();
  }

  private static MessageTypeCatalogEntry _ephemeral(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null) { Ephemeral = new EphemeralInfo(default, default) };

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
