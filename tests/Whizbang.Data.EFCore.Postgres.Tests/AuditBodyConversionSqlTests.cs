// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>An audited type that keeps its body in its audit records.</summary>
public sealed record AuditBodyKeptProbe : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid Id { get; init; }
}

/// <summary>
/// #2326: audit records written before the pointer default each carry a full copy of the audited event, and nothing
/// converted them. Maintenance walks the audit records in bounded, resumable batches and removes the copy, keeping the
/// records of types declared <c>[AuditEvent(IncludeBody = true)]</c> as they are.
/// </summary>
/// <docs>fundamentals/events/system-events#audit-body</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/AuditBodyConversionStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/444_HistoricalAuditRecordsBecomePointers.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class AuditBodyConversionSqlTests : EFCoreTestBase {
  [Before(Test)]
  public void RegisterKeptType() => AuditEventRegistry.Register(typeof(AuditBodyKeptProbe), new AuditEventAttribute { IncludeBody = true });

  [Test]
  public async Task Step_ConvertsAHistoricalRecordToThePointerForm_AndKeepsIncludeBodyTypesAsync() {
    await using var conn = await _openAsync();
    var original = (Guid)TrackedGuid.New();
    var converted = await _recordAsync(conn, "OrderPlaced", original);
    var kept = await _recordAsync(conn, TypeNameFormatter.SimpleName(typeof(AuditBodyKeptProbe)), (Guid)TrackedGuid.New());

    var result = await new AuditBodyConversionStep(NullLogger.Instance, 100, 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(result.RecordsConverted).IsEqualTo(1L);
    await Assert.That(result.RecordsKept).IsEqualTo(1L);
    await Assert.That(result.Status).IsEqualTo("finished");
    await Assert.That(await _hasBodyAsync(conn, converted)).IsFalse();
    await Assert.That(await _hasBodyAsync(conn, kept)).IsTrue().Because("its type keeps the body in its audit records");
    using var document = JsonDocument.Parse(await _dataAsync(conn, converted));
    var record = document.RootElement;
    await Assert.That(record.GetProperty("OriginalEventId").GetGuid()).IsEqualTo(original)
      .Because("the pointer still finds the audited event, so readers see the same entry");
    await Assert.That(record.GetProperty("OriginalEventType").GetString()).IsEqualTo("OrderPlaced");
  }

  [Test]
  public async Task Step_ConvertsInBoundedBatches_Resumes_AndReportsWhatIsLeftAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 5; i++) {
      await _recordAsync(conn, "OrderPlaced", (Guid)TrackedGuid.New());
    }

    var first = await new AuditBodyConversionStep(NullLogger.Instance, 2, 1).ConvertAsync(_services(), CancellationToken.None);
    var pendingPartway = await _pendingAsync(conn);
    var rest = await new AuditBodyConversionStep(NullLogger.Instance, 2, 10).ConvertAsync(_services(), CancellationToken.None);
    var again = await new AuditBodyConversionStep(NullLogger.Instance, 2, 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(first.Batches).IsEqualTo(1);
    await Assert.That(first.RecordsConverted).IsEqualTo(2L);
    await Assert.That(first.RecordsLeft).IsEqualTo(3L);
    await Assert.That(pendingPartway).IsEqualTo(3L).Because("the remainder reaches whizbang.maintenance.pending_rows");
    await Assert.That(rest.RecordsConverted).IsEqualTo(3L).Because("the next cycle resumes past the first window");
    await Assert.That(again.RecordsConverted).IsEqualTo(0L);
    await Assert.That(await _pendingAsync(conn)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_data ? 'OriginalBody'")).IsEqualTo(0L);
  }

  [Test]
  public async Task Step_UnderAuditBodyDefaultInclude_ConvertsNothingAsync() {
    await using var conn = await _openAsync();
    var record = await _recordAsync(conn, "OrderPlaced", (Guid)TrackedGuid.New());
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(new SystemEventOptions { AuditBodyDefault = AuditBodyDefault.Include }))
      .BuildServiceProvider();

    var result = await new AuditBodyConversionStep(NullLogger.Instance, 100, 10).ConvertAsync(services, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(AuditBodyConversionStep.SKIPPED_INCLUDE);
    await Assert.That(await _hasBodyAsync(conn, record)).IsTrue();
  }

  [Test]
  public async Task KeptBodyNames_AreBothFormsOfEachIncludeBodyType_SortedAsync() {
    var names = AuditBodyConversionStep.KeptBodyNames(new Catalog(
      new MessageTypeCatalogEntry(typeof(AuditBodyKeptProbe), TypeNameFormatter.FormatClrTypeName(typeof(AuditBodyKeptProbe)), "event", null),
      new MessageTypeCatalogEntry(typeof(EventAudited), TypeNameFormatter.FormatClrTypeName(typeof(EventAudited)), "event", null)));

    await Assert.That(names).IsEquivalentTo(new[] {
      TypeNameFormatter.SimpleName(typeof(AuditBodyKeptProbe)), TypeNameFormatter.GetFullName(TypeNameFormatter.Format(typeof(AuditBodyKeptProbe))),
    }.Order(StringComparer.Ordinal));
    await Assert.That(AuditBodyConversionStep.KeptBodyNames(null)).IsEmpty();
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    var withLogging = new ServiceCollection().AddLogging().BuildServiceProvider();

    await Assert.That(AuditBodyConversionStep.FromServices(withLogging).Name).IsEqualTo("audit-body-conversion");
    await Assert.That(AuditBodyConversionStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => AuditBodyConversionStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new AuditBodyConversionStep(null!, 1, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await AuditBodyConversionStep.FromServices(withLogging).ConvertAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await AuditBodyConversionStep.FromServices(withLogging).RunAsync(_services(), CancellationToken.None);
    var withoutCatalog = await AuditBodyConversionStep.FromServices(withLogging)
      .ConvertAsync(new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider(), CancellationToken.None);
    await Assert.That(withoutCatalog.RecordsConverted).IsEqualTo(0L).Because("an empty store has nothing to convert");
  }

  /// <summary>An audit record as it was written before the pointer default: carrying the audited body.</summary>
  private async Task<Guid> _recordAsync(NpgsqlConnection conn, string originalType, Guid originalEventId) {
    var id = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<EventAudited> {
      MessageId = MessageId.New(),
      Payload = new EventAudited {
        Id = id,
        OriginalEventId = originalEventId,
        OriginalEventType = originalType,
        OriginalStreamId = Guid.Empty.ToString(),
        OriginalStreamPosition = 1,
        OriginalBody = JsonElement.Parse("""{"amount": 42, "note": "a full copy of the audited event"}"""),
        Timestamp = DateTimeOffset.UnixEpoch,
      },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(id, envelope);
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    return envelope.MessageId.Value;
  }

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services() =>
    new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton<IMessageTypeCatalog>(new Catalog(
        new MessageTypeCatalogEntry(typeof(AuditBodyKeptProbe), TypeNameFormatter.FormatClrTypeName(typeof(AuditBodyKeptProbe)), "event", null)))
      .BuildServiceProvider();

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<long> _pendingAsync(NpgsqlConnection conn) =>
    await _scalarAsync(conn, "SELECT pending FROM wh_pending_reclamation(100) WHERE task_name = 'convert_audit_record_bodies'");

  private static async Task<bool> _hasBodyAsync(NpgsqlConnection conn, Guid eventId) =>
    await _scalarAsync(conn, $"SELECT count(*) FROM wh_event_body WHERE event_id = '{eventId}' AND event_data ? 'OriginalBody'") == 1;

  private static async Task<string> _dataAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT event_data::text FROM wh_event_body WHERE event_id = '{eventId}'";
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
