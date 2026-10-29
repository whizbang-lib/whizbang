// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Policies;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1874 on the Dapper driver: rows an earlier release stored under C# names that differ from the declared
/// <c>[JsonPropertyName]</c> only in case are rewritten to the declared spelling at startup, at any depth and inside
/// collections of records, and read back with their real values; a row holding both spellings with different values
/// stops startup; a start that finds nothing to change updates no row; a required member missing from a stored row
/// fails the read rather than reading as zero; and an event stored under the C# names replays with its values.
/// </summary>
/// <docs>operations/upgrading/declared-json-names</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeySpellingPass.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[NotInParallel("PostgreSQL")]
public class DapperDeclaredNameSpellingTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_declared_picture";
  private const string EMPTY = "{}";
  private const string OLD_PICTURE =
    """{"Id":"__ID__","Title":"sketch","Objects":[{"Color":"red","Points":[{"X":327,"Y":362},{"X":330.7060738825059,"Y":362}]}]}""";

  private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

  private static KeyValuePair<string, string>[] _entries() => [..
    Whizbang.Generated.PerspectiveSchemas.Entries.Where(e => e.Key == nameof(DapperDeclaredPictureProjection))];

  private static IReadOnlyList<DeclaredKeyTable> _tables() =>
    DeclaredKeyTable.ForSerializedModels(PerspectiveDocumentModels.Registered.Where(m => m.Table == TABLE), _options);

  private Task _startAsync(ILogger? logger = null) =>
    new PostgresSchemaInitializer(ConnectionString, _entries()) { DeclaredKeyTables = _tables(), Logger = logger }
      .InitializeSchemaAsync();

  private DapperPostgresPerspectiveStore<DapperDeclaredPicture> _store() => new(ConnectionString, TABLE, _options);

  [Test]
  public async Task TheGeneratedSchema_RegistersTheModelItsTableStoresAsync() {
    await Assert.That(PerspectiveDocumentModels.Registered.Any(m => m.Table == TABLE && m.Model == typeof(DapperDeclaredPicture)))
      .IsTrue().Because("the driver needs the model's type to know which names it declares, and reflection is not an option");
  }

  /// <summary>Through the registration a host makes: the table the generated schema creates, rewritten at the next start.</summary>
  [Test]
  public async Task RowsStoredWithCSharpNames_AreRewrittenAtHostStart_AndReadBackAsync() {
    await _hostStartAsync();
    var id = Guid.CreateVersion7();
    await _insertAsync(id, OLD_PICTURE.Replace("__ID__", id.ToString(), StringComparison.Ordinal));

    await _hostStartAsync();

    await Assert.That(await _scalarAsync($"SELECT data #>> '{{Objects,0,Points,1,x}}' FROM {TABLE} WHERE id = '{id}'"))
      .IsEqualTo("330.7060738825059");
    await Assert.That(await _scalarAsync($"SELECT (data ? 'title')::text || (data ? 'Title')::text FROM {TABLE} WHERE id = '{id}'"))
      .IsEqualTo("truefalse");
    var read = await _store().GetByStreamIdAsync(id);
    await Assert.That(read!.Title).IsEqualTo("sketch");
    await Assert.That(read.Objects[0].Color).IsEqualTo("red");
    await Assert.That(read.Objects[0].Points[0].X).IsEqualTo(327d);
    await Assert.That(read.Objects[0].Points[1].Y).IsEqualTo(362d);
  }

  [Test]
  public async Task ARowHoldingBothSpellings_StopsStartup_NamingItAsync() {
    await _startAsync();
    var id = Guid.CreateVersion7();
    await _insertAsync(id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":5,"X":6,"y":7}]}]}""");

    var failure = await Assert.That(() => _startAsync()).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains($"{TABLE}.data").And.Contains(id.ToString())
      .And.Contains("$.Objects[0].Points[0]").And.Contains("\"X\"").And.Contains("\"x\"");
  }

  [Test]
  public async Task ASecondStart_ChangesNoRowAsync() {
    await _startAsync();
    var id = Guid.CreateVersion7();
    await _insertAsync(id, OLD_PICTURE.Replace("__ID__", id.ToString(), StringComparison.Ordinal));
    var log = new ListLogger();
    await _startAsync(log);
    var afterRewrite = await _scalarAsync($"SELECT xmin::text FROM {TABLE} WHERE id = '{id}'");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("declared spelling", StringComparison.Ordinal)
        && e.Message.Contains("$.Objects[*].Points[*]: X -> x", StringComparison.Ordinal))).IsTrue();

    await _startAsync();
    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT xmin::text FROM {TABLE} WHERE id = '{id}'")).IsEqualTo(afterRewrite);
    await Assert.That(await _scalarAsync($"SELECT settled_at IS NOT NULL FROM wh_stored_form_migrations WHERE name LIKE 'declared-names:{TABLE}:%'"))
      .IsEqualTo("True");
  }

  [Test]
  public async Task ARequiredMemberMissing_FailsTheRead_AndIsReportedAtStartupAsync() {
    await _startAsync();
    var id = Guid.CreateVersion7();
    await _insertAsync(id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":3}]}]}""");
    var log = new ListLogger();

    await _startAsync(log);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("$.Objects[*].Points[*].y", StringComparison.Ordinal)))
      .IsTrue();
    await Assert.That(async () => await _store().GetByStreamIdAsync(id)).Throws<JsonException>()
      .Because("a missing required member is refused, not read as zero");
  }

  /// <summary>Events are history and are never rewritten; an event stored under the C# names still replays with its values.</summary>
  [Test]
  public async Task AnEventStoredWithCSharpNames_ReplaysWithItsValuesAsync() {
    var store = new DapperPostgresEventStore(
      ConnectionFactory, Executor, _options, new EventEnvelopeJsonbAdapter(_options),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance), new PolicyEngine(), null,
      NullLogger<DapperPostgresEventStore>.Instance);
    var streamId = Guid.CreateVersion7();
    var envelope = new MessageEnvelope<DapperDeclaredPointsDrawn> {
      MessageId = MessageId.New(),
      Payload = new DapperDeclaredPointsDrawn(streamId, [new DapperDeclaredPoint { X = 1, Y = 2 }]),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "declared-names", InstanceId = Guid.NewGuid(), HostName = "host", ProcessId = 1 },
    });
    await store.AppendAsync(streamId, envelope);
    await _execAsync($$"""
      UPDATE wh_event_body SET event_data = '{"Id":"{{streamId}}","Points":[{"X":327,"Y":362}]}'
      WHERE event_id IN (SELECT event_id FROM wh_event_store WHERE stream_id = '{{streamId}}')
      """);

    var replayed = new List<DapperDeclaredPointsDrawn>();
    await foreach (var read in store.ReadAsync<DapperDeclaredPointsDrawn>(streamId, 0)) {
      replayed.Add(read.Payload);
    }

    await Assert.That(replayed.Count).IsEqualTo(1);
    await Assert.That(replayed[0].Points[0].X).IsEqualTo(327d);
    await Assert.That(replayed[0].Points[0].Y).IsEqualTo(362d);
  }

  private async Task _hostStartAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(ConnectionString, _options, true, _entries(), managedObjects: null);
    await using var provider = services.BuildServiceProvider();
    await provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single().StartAsync(CancellationToken.None);
  }

  private Task _insertAsync(Guid id, string data) => _execAsync($"""
    INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
    VALUES ('{id}', '{data}', '{EMPTY}', '{EMPTY}', now(), now(), 1)
    """);

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.ExecuteAsync(sql);
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    return (await db.ExecuteScalarAsync(sql))?.ToString() ?? "<null>";
  }

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }
}
