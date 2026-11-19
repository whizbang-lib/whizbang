// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>An ephemeral event type that moved here from another assembly.</summary>
public sealed record MovedAssemblyWalkProbePinged : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>An event type as it was named before a rename within this assembly.</summary>
public sealed record RenamedInAssemblyWalkProbeFormer : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>
/// #2264: a type that moved between assemblies stored its events under its old assembly (<c>Ns.Type, OldAssembly</c>), and
/// the pinned-type ledger records that former name with the assembly. The ephemeral reclassify walks the type's history
/// under it, and the type-definition reconciler keys the type's rewind grace by it. A bare former name (a rename within
/// the assembly) still takes the type's current assembly. Events are written through the event store's append path.
/// </summary>
/// <docs>fundamentals/identity/pinned-type-ledger#a-type-that-moves-to-another-assembly</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Fingerprint/TypeDefinitionReconciler.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/TypeNameFormatter.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class FormerNameFromAnotherAssemblyWalkTests : EFCoreTestBase {
  // The type never lived anywhere but this test assembly, so the assembly it moved from is simulated: its events are
  // appended through the real path, then relabeled with this assembly in place of the real one, as an older build in the
  // old assembly would have stored them.
  private const string FORMER_ASSEMBLY = "Former.Contracts";
  private static readonly Type _moved = typeof(MovedAssemblyWalkProbePinged);
  private static readonly string _formerStoredName = $"{TypeNameFormatter.FormatClrTypeName(_moved)}, {FORMER_ASSEMBLY}";

  [Test]
  public async Task TheReclassifyWalk_ReadsTheHistoryOfATypeThatMovedAssemblies_UnderItsFormerStoredNameAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _storeAsync(conn, stream, new MovedAssemblyWalkProbePinged { SessionId = stream });
    await _executeAsync(conn, "UPDATE wh_event_store SET event_type = @former WHERE stream_id = @stream",
      ("former", _formerStoredName), ("stream", stream));

    await new EphemeralHistoryReclassifyStep(NullLogger.Instance, 10, 10)
      .ReclassifyAsync(_services(_movedEntry()), CancellationToken.None);

    await Assert.That(await _walkedAsync(conn, _formerStoredName)).IsEqualTo(1L)
      .Because("the walk under the former stored name read the event the old assembly stored");
    await Assert.That(await _ephemeralFlagAsync(conn, stream)).IsEqualTo(8).Because("the old assembly's history was reclassified");
  }

  [Test]
  public async Task TheReclassifyWalk_TakesTheCurrentAssembly_ForABareFormerNameAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _storeAsync(conn, stream, new RenamedInAssemblyWalkProbeFormer { SessionId = stream });
    var entry = new MessageTypeCatalogEntry(_moved, TypeNameFormatter.FormatClrTypeName(_moved), "event", null) {
      Ephemeral = new EphemeralInfo(default, default),
      FormerNames = [TypeNameFormatter.FormatClrTypeName(typeof(RenamedInAssemblyWalkProbeFormer))],
    };

    await new EphemeralHistoryReclassifyStep(NullLogger.Instance, 10, 10).ReclassifyAsync(_services(entry), CancellationToken.None);

    await Assert.That(await _walkedAsync(conn, TypeNameFormatter.Format(typeof(RenamedInAssemblyWalkProbeFormer)))).IsEqualTo(1L);
    await Assert.That(await _ephemeralFlagAsync(conn, stream)).IsEqualTo(8);
  }

  [Test]
  public async Task TheRewindGrace_IsKeyedByTheFormerStoredName_TheOldAssemblysEventsCarryAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _storeAsync(conn, stream, new MovedAssemblyWalkProbePinged { SessionId = stream });
    await _executeAsync(conn, "UPDATE wh_event_store SET event_type = @former WHERE stream_id = @stream",
      ("former", _formerStoredName), ("stream", stream));
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var entry = _movedEntry() with { Ephemeral = new EphemeralInfo(default, default, RewindGraceSeconds: 42) };

    await new TypeDefinitionReconciler(new SingleCoordinatorScopeFactory(coordinator),
        Options.Create(new EphemeralOptions()), NullLogger<TypeDefinitionReconciler>.Instance, new Catalog(entry))
      .ReconcileAsync();

    var graced = await _textsAsync(conn,
      "SELECT g.event_type FROM wh_ephemeral_type_grace g JOIN wh_event_store es ON es.event_type = g.event_type " +
      "WHERE es.stream_id = @stream AND g.grace_seconds = 42", ("stream", stream));
    await Assert.That(graced).IsEquivalentTo([_formerStoredName])
      .Because("the reaper joins the grace to event_type, so the old assembly's events take the type's grace only by that name");
  }

  // ===== Helpers =====

  private static MessageTypeCatalogEntry _movedEntry() =>
    new(_moved, TypeNameFormatter.FormatClrTypeName(_moved), "event", "55555555-5555-5555-5555-555555555555") {
      Ephemeral = new EphemeralInfo(default, default),
      // As the ledger records it once the entry moved here with its type.
      FormerNames = [_formerStoredName],
      SettingsHash = new string('a', 64),
      SchemaHash = new string('c', 64),
    };

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  // The reconciler resolves the scoped coordinator from a scope per pass; this hands it the test's coordinator.
  private sealed class SingleCoordinatorScopeFactory(IWorkCoordinator coordinator) : IServiceScopeFactory, IServiceScope, IServiceProvider {
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) => serviceType == typeof(IWorkCoordinator) ? coordinator : null;
    public void Dispose() { }
  }

  private ServiceProvider _services(MessageTypeCatalogEntry entry) =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }))
      .AddSingleton<IMessageTypeCatalog>(new Catalog(entry))
      .BuildServiceProvider();

  private static async Task<long> _walkedAsync(NpgsqlConnection conn, string eventType) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(sum(events_matched), 0) FROM wh_type_history_walks WHERE operation = 'ephemeral' AND event_type = @t";
    cmd.Parameters.AddWithValue("t", eventType);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<int> _ephemeralFlagAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flags FROM wh_event_store WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (int)(await cmd.ExecuteScalarAsync())! & 8;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>An event stored through the event store's append path and numbered by the commit-order stamper.</summary>
  private async Task _storeAsync<T>(NpgsqlConnection conn, Guid stream, T payload) where T : IEvent {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
  }

  private static async Task<List<string>> _textsAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var texts = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      texts.Add(reader.GetString(0));
    }
    return texts;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
