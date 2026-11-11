// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
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
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2254 guard: every maintenance step that walks a type's stored history names its targets the way the append path
/// stores the type's events. A walk named any other way (the bare CLR name, say) reads nothing through the type's index
/// and completes with every count at 0, so the step silently does nothing. The events here are written through the event
/// store's own append path; the walk rows each step leaves must be keyed by a stored <c>event_type</c>, and must have read
/// the events. A new step that walks history fails the source scan until it is added here.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class HistoryWalkTargetNamingTests : EFCoreTestBase {
  /// <summary>Every source file that walks a type's history, each covered by a run below.</summary>
  private static readonly string[] _walkingSteps = [
    "src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs",
    "src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs",
  ];

  [Test]
  public async Task EveryHistoryWalkStep_IsCoveredByThisGuardAsync() {
    var root = _findRepoRoot();
    var walkers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .Where(f => File.ReadAllText(f).Contains("new TypeHistoryWalker(", StringComparison.Ordinal))
      .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
      .ToList();

    await Assert.That(walkers).Contains("src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs")
      .Because("the scan must recognize a walking step, or it is not reading the source tree");
    await Assert.That(walkers).IsEquivalentTo(_walkingSteps)
      .Because("a step that walks history must be run by EveryHistoryWalkStep_NamesItsTargetsAsTheAppendPathStores");
  }

  [Test]
  public async Task EveryHistoryWalkStep_NamesItsTargetsAsTheAppendPathStoresThem_AndReadsThemAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _storeAsync(conn, stream, new EphemeralHistoryProbePinged { SessionId = stream });
    var other = (Guid)TrackedGuid.New();
    await _storeAsync(conn, other, new SupersedingHistoryProbeProfile { UserId = other, Revision = 1 });
    var services = _services();

    await new EphemeralHistoryReclassifyStep(NullLogger.Instance, 10, 10).ReclassifyAsync(services, CancellationToken.None);
    await new SupersedingHistoryReconcileStep(NullLogger.Instance, 10, 10, () => [typeof(SupersedingHistoryProbeProfile)])
      .ReconcileAsync(services, CancellationToken.None);

    foreach (var operation in new[] { "ephemeral", "superseding" }) {
      var walks = await _textsAsync(conn,
        "SELECT event_type FROM wh_type_history_walks WHERE operation = @o AND events_total > 0 AND events_matched > 0", ("o", operation));
      await Assert.That(walks).IsNotEmpty().Because($"the {operation} step walked a type and read its events");
      await Assert.That(await _walksNamingNoStoredTypeAsync(conn, operation)).IsEmpty()
        .Because($"every {operation} walk is keyed by a name the append path stored");
    }
  }

  /// <summary>The positive control: a walk named by the bare CLR name is one the guard reports.</summary>
  [Test]
  public async Task TheGuard_ReportsAWalkNamedOtherThanTheStoreDoesAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _storeAsync(conn, stream, new EphemeralHistoryProbePinged { SessionId = stream });
    var bare = TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbePinged));
    await _executeAsync(conn, "SELECT wh_type_history_ensure('ephemeral', @t, @t)", ("t", bare));

    await Assert.That(await _walksNamingNoStoredTypeAsync(conn, "ephemeral")).IsEquivalentTo([bare]);
  }

  // ===== Helpers =====

  private static Task<List<string>> _walksNamingNoStoredTypeAsync(NpgsqlConnection conn, string operation) =>
    _textsAsync(conn,
      "SELECT h.event_type FROM wh_type_history_walks h WHERE h.operation = @o " +
      "AND NOT EXISTS (SELECT 1 FROM wh_event_store es WHERE es.event_type = h.event_type)", ("o", operation));

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private ServiceProvider _services() {
    var type = typeof(EphemeralHistoryProbePinged);
    return new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }))
      .AddSingleton<IMessageTypeCatalog>(new Catalog(new MessageTypeCatalogEntry(type, TypeNameFormatter.FormatClrTypeName(type), "event", null) {
        Ephemeral = new EphemeralInfo(default, default),
      }))
      .BuildServiceProvider();
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

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }
}
