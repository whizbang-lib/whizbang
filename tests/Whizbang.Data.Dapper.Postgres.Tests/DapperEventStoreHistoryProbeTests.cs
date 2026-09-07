using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>Two contracts on one stream; the perspective under test folds only the handled one.</summary>
public static class HistoryProbeContracts {
  public sealed record HandledEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
  }

  public sealed record OtherEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
  }
}

/// <summary>
/// The resurrection-on-wake history probe on the Dapper driver: the untyped form ("any earlier
/// event") and the perspective-aware form of issue #696 ("an earlier event of a type this
/// perspective folds"). The Dapper store served the interface default (false) for both, so
/// resurrection never fired through it. Mirrors <c>EventStoreHistoryProbeSqlTests</c> on the EF
/// Core driver.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
public class DapperEventStoreHistoryProbeTests : PostgresTestBase {
#pragma warning disable CA1859 // Interface typing is the point: the runner holds an IEventStore, and the typed probe is an interface member.
  private IEventStore _store() {
#pragma warning restore CA1859
    var jsonOptions = JsonOptionsHelper.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private async Task _seedPointerAsync(Guid eventId, Guid streamId, long version, string eventType = "TestNamespace.ProbeEvent") {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'TestAggregate', @eventType, @version, NOW() - INTERVAL '61 days')",
      new { eventId, streamId, eventType, version });
  }

  [Test]
  public async Task Probe_StreamWithOlderEvents_ReturnsTrueAsync() {
    var streamId = Guid.NewGuid();
    await _seedPointerAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), streamId, 1);

    await Assert.That(await _store().HasStreamEventsBeforeAsync(streamId, Guid.CreateVersion7())).IsTrue()
      .Because("the Dapper driver must answer the probe, not serve the interface default");
  }

  [Test]
  public async Task Probe_NewStream_ReturnsFalseAsync() {
    await Assert.That(await _store().HasStreamEventsBeforeAsync(Guid.NewGuid(), Guid.CreateVersion7())).IsFalse();
  }

  [Test]
  public async Task TypedProbe_OlderEventsOfOtherTypesOnly_ReturnsFalseAsync() {
    var streamId = Guid.NewGuid();
    await _seedPointerAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), streamId, 1,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.OtherEvent)));

    await Assert.That(await _store().HasStreamEventsBeforeAsync(streamId, Guid.CreateVersion7(), [typeof(HistoryProbeContracts.HandledEvent)])).IsFalse()
      .Because("history this perspective never folded is a first contact, not a reaped row");
  }

  [Test]
  public async Task TypedProbe_OlderEventOfHandledType_ReturnsTrueAsync() {
    var streamId = Guid.NewGuid();
    await _seedPointerAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-62)), streamId, 1,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.OtherEvent)));
    await _seedPointerAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), streamId, 2,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.HandledEvent)));

    await Assert.That(await _store().HasStreamEventsBeforeAsync(streamId, Guid.CreateVersion7(), [typeof(HistoryProbeContracts.HandledEvent)])).IsTrue();
  }

  [Test]
  public async Task TypedProbe_MatchesStoredEventTypeFormat_NestedAndAssemblyQualifiedAsync() {
    var streamId = Guid.NewGuid();
    var decorated = typeof(HistoryProbeContracts.HandledEvent).AssemblyQualifiedName!;
    await Assert.That(decorated).Contains("+").And.Contains("Version=");
    await _seedPointerAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), streamId, 1, decorated);

    await Assert.That(await _store().HasStreamEventsBeforeAsync(streamId, Guid.CreateVersion7(), [typeof(HistoryProbeContracts.HandledEvent)])).IsTrue()
      .Because("the match goes through EventTypeMatchingHelper, which normalizes the decorated form");
  }
}
