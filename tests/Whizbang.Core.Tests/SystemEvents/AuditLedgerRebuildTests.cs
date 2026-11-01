// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Issue #1724 (WS-2 S7): audit records can be re-derived. A rebuild selects stored events the way a redelivery does,
/// keeps only the types audit eligibility admits, and mints each record under an id derived from the event it audits,
/// so deriving the ledger again yields one record per origin event.
/// </summary>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditLedgerRebuild.cs</code-under-test>
[Category("SystemEvents")]
public class AuditLedgerRebuildTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _eventTime = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
  private static readonly Guid _correlation = new("0190f1a2-0000-7000-8000-00000000c0c0");
  private static readonly Guid _causation = new("0190f1a2-0000-7000-8000-00000000ca5e");
  private const string TENANT_SCOPE = """{"t":"tenant-a","u":"user-1","c":"customer-9","o":"org-3"}""";

  internal sealed record RebuildOrderPlaced(decimal Amount) : IEvent;

  [AuditEvent(Exclude = true, Reason = "heartbeat")]
  internal sealed record RebuildHeartbeat : IEvent;

  [AuditEvent(Reason = "money moved", Level = Whizbang.Core.Audit.AuditLevel.Critical)]
  internal sealed record RebuildPaymentTaken : IEvent;

  [AuditEvent(IncludeBody = true)]
  internal sealed record RebuildBodyKept : IEvent;

  internal sealed record RebuildPresence : IEphemeralEvent;

  [AuditEvent(IncludeBody = true)]
  internal sealed record RebuildPresenceKept : IEphemeralEvent;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      _entry(typeof(RebuildOrderPlaced)), _entry(typeof(RebuildHeartbeat)), _entry(typeof(RebuildPaymentTaken)),
      _entry(typeof(RebuildBodyKept)), _entry(typeof(RebuildPresence)), _entry(typeof(RebuildPresenceKept)),
      _entry(typeof(EventAudited)),
    ];

    private static MessageTypeCatalogEntry _entry(Type type) => new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null);
  }

  private static AuditLedgerRebuild _rebuild(Action<SystemEventOptions>? configure = null) {
    var options = new SystemEventOptions();
    options.EnableEventAudit();
    configure?.Invoke(options);
    return new AuditLedgerRebuild(new Catalog(), options, new FakeTimeProvider(_now));
  }

  private static string _metadata(Guid eventId) =>
    $$"""{"MessageId":"{{eventId}}","Hops":[{"ty":0,"ts":"2026-03-04T05:06:07+00:00","co":"{{_correlation}}","ca":"{{_causation}}"}]}""";

  private static RedeliveryEvent _stored(
      Type type, Guid? eventId = null, string? scope = TENANT_SCOPE, string? metadata = null, bool useDefaultMetadata = true,
      EventFlags flags = EventFlags.None, long version = 7, string data = """{"Amount":12}""") {
    var id = eventId ?? Guid.NewGuid();
    return new RedeliveryEvent {
      EventId = id,
      StreamId = new Guid("0190f1a2-0000-7000-8000-0000000057e4"),
      Version = version,
      CommitSequence = 42,
      EventType = TypeNameFormatter.Format(type),
      EventData = data,
      Metadata = metadata ?? (useDefaultMetadata ? _metadata(id) : null),
      Scope = scope,
      Flags = (int)flags,
    };
  }

  private static JsonElement _record(OutboxMessage message) => message.Envelope.Payload;

  #region What a re-derived record holds

  [Test]
  public async Task Rederive_AnEligibleEvent_IsARecordOfItOnTheTenantLedgerAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced));

    var message = _rebuild().Rederive(stored)!;
    var record = _record(message);

    await Assert.That(record.GetProperty("Id").GetGuid()).IsEqualTo(AuditLedger.RecordIdFor(stored.EventId));
    await Assert.That(record.GetProperty("OriginalEventId").GetGuid()).IsEqualTo(stored.EventId);
    await Assert.That(record.GetProperty("OriginalEventType").GetString())
      .IsEqualTo(TypeNameFormatter.FormatClrTypeName(typeof(RebuildOrderPlaced)));
    await Assert.That(record.GetProperty("OriginalStreamId").GetString()).IsEqualTo(stored.StreamId.ToString());
    await Assert.That(record.GetProperty("OriginalStreamPosition").GetInt64()).IsEqualTo(7L);
    await Assert.That(record.GetProperty("TenantId").GetString()).IsEqualTo("tenant-a");
    await Assert.That(record.GetProperty("UserId").GetString()).IsEqualTo("user-1");
    await Assert.That(record.GetProperty("Scope").GetProperty("TenantId").GetString()).IsEqualTo("tenant-a");
    await Assert.That(message.StreamId).IsEqualTo(AuditLedger.StreamFor("tenant-a"));
    await Assert.That(message.Destination).IsEqualTo(AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
    await Assert.That(message.Scope!.CustomerId).IsEqualTo("customer-9");
    await Assert.That(message.Scope.OrganizationId).IsEqualTo("org-3");
    await Assert.That(message.IsEvent).IsFalse();
  }

  [Test]
  public async Task Rederive_TheRecordTime_IsWhenTheEventHappenedNotWhenItWasRebuiltAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildOrderPlaced)))!);

    await Assert.That(record.GetProperty("Timestamp").GetDateTimeOffset()).IsEqualTo(_eventTime)
      .Because("an audit trail is read by when things happened; a rebuild must not move every record to the rebuild's hour");
  }

  [Test]
  public async Task Rederive_CorrelationAndCausation_ComeFromTheStoredHopAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildOrderPlaced)))!);

    await Assert.That(record.GetProperty("CorrelationId").GetString()).IsEqualTo(_correlation.ToString());
    await Assert.That(record.GetProperty("CausationId").GetString()).IsEqualTo(_causation.ToString());
  }

  [Test]
  public async Task Rederive_TheAttributesReasonAndLevel_AreOnTheRecordAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildPaymentTaken)))!);

    await Assert.That(record.GetProperty("AuditReason").GetString()).IsEqualTo("money moved");
    await Assert.That(record.GetProperty("AuditLevel").GetInt32()).IsEqualTo((int)Whizbang.Core.Audit.AuditLevel.Critical);
  }

  [Test]
  public async Task Rederive_ByDefault_LeavesTheBodyOutAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildOrderPlaced)))!);

    await Assert.That(record.TryGetProperty("OriginalBody", out _)).IsFalse();
  }

  [Test]
  public async Task Rederive_ATypeThatKeepsItsBody_CarriesTheStoredBodyAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildBodyKept), data: """{"Note":"kept"}"""))!);

    await Assert.That(record.GetProperty("OriginalBody").GetProperty("Note").GetString()).IsEqualTo("kept");
  }

  [Test]
  public async Task Rederive_TheDefaultSetToInclude_CarriesEveryBodyAsync() {
    var rebuild = _rebuild(o => o.AuditBodyDefault = AuditBodyDefault.Include);

    var record = _record(rebuild.Rederive(_stored(typeof(RebuildOrderPlaced)))!);

    await Assert.That(record.GetProperty("OriginalBody").GetProperty("Amount").GetInt32()).IsEqualTo(12);
  }

  #endregion

  #region Which events are re-derived

  [Test]
  public async Task Rederive_AnExcludedType_IsNoRecordAsync() {
    await Assert.That(_rebuild().Rederive(_stored(typeof(RebuildHeartbeat)))).IsNull();
  }

  [Test]
  public async Task Rederive_OptIn_AnUnmarkedType_IsNoRecordAsync() {
    var rebuild = _rebuild(o => o.AuditMode = AuditMode.OptIn);

    await Assert.That(rebuild.Rederive(_stored(typeof(RebuildOrderPlaced)))).IsNull();
    await Assert.That(rebuild.Rederive(_stored(typeof(RebuildPaymentTaken)))).IsNotNull()
      .Because("the control: a marked type is re-derived under the same mode");
  }

  [Test]
  public async Task Rederive_AStoredAuditRecord_IsNoRecordAsync() {
    await Assert.That(_rebuild().Rederive(_stored(typeof(EventAudited)))).IsNull()
      .Because("auditing an audit record is the self-audit loop, on a rebuild as everywhere else");
  }

  [Test]
  public async Task Rederive_AnEphemeralEvent_IsNoRecordAsync() {
    await Assert.That(_rebuild().Rederive(_stored(typeof(RebuildPresence), flags: EventFlags.Ephemeral))).IsNull();
  }

  [Test]
  public async Task Rederive_AnEphemeralEventThatKeepsItsBody_IsARecordWithItAsync() {
    var message = _rebuild().Rederive(_stored(typeof(RebuildPresenceKept), flags: EventFlags.Ephemeral, data: """{"Typing":true}"""));

    await Assert.That(_record(message!).GetProperty("OriginalBody").GetProperty("Typing").GetBoolean()).IsTrue();
  }

  [Test]
  public async Task Rederive_AnUnknownType_IsARecordUnderOptOutAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced)) with { EventType = "Gone.Renamed, Gone" };

    var message = _rebuild().Rederive(stored);

    await Assert.That(_record(message!).GetProperty("OriginalEventType").GetString()).IsEqualTo("Gone.Renamed")
      .Because("OptOut audits every type nothing excludes, and nothing can exclude a type this build does not know");
  }

  [Test]
  public async Task Rederive_AnUnknownType_IsNoRecordUnderOptInAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced)) with { EventType = "Gone.Renamed, Gone" };

    await Assert.That(_rebuild(o => o.AuditMode = AuditMode.OptIn).Rederive(stored)).IsNull()
      .Because("OptIn audits only marked types, and a type this build does not know carries no mark");
  }

  [Test]
  public async Task Rederive_AnUnknownEphemeralType_IsNoRecordAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced), flags: EventFlags.Ephemeral) with { EventType = "Gone.Renamed, Gone" };

    await Assert.That(_rebuild().Rederive(stored)).IsNull();
  }

  #endregion

  #region Stored rows with less to go on

  [Test]
  public async Task Rederive_NoStoredMetadata_StampsTheRebuildTimeAndNoChainAsync() {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildOrderPlaced), useDefaultMetadata: false))!);

    await Assert.That(record.GetProperty("Timestamp").GetDateTimeOffset()).IsEqualTo(_now);
    await Assert.That(record.GetProperty("CorrelationId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(record.GetProperty("CausationId").ValueKind).IsEqualTo(JsonValueKind.Null);
  }

  [Test]
  [Arguments("""{"MessageId":"x"}""")]
  [Arguments("""{"MessageId":"x","Hops":[]}""")]
  [Arguments("""{"MessageId":"x","Hops":{}}""")]
  [Arguments("""{"MessageId":"x","Hops":[{"ty":0}]}""")]
  [Arguments("""{"MessageId":"x","Hops":[{"ts":5,"co":5,"ca":5}]}""")]
  [Arguments("""{"MessageId":"x","Hops":[{"ts":"not a time"}]}""")]
  [Arguments("""{"MessageId":"x","Hops":[7]}""")]
  [Arguments("null")]
  public async Task Rederive_MetadataWithoutAUsableHop_FallsBackAsync(string metadata) {
    var record = _record(_rebuild().Rederive(_stored(typeof(RebuildOrderPlaced), metadata: metadata))!);

    await Assert.That(record.GetProperty("Timestamp").GetDateTimeOffset()).IsEqualTo(_now);
    await Assert.That(record.GetProperty("CorrelationId").ValueKind).IsEqualTo(JsonValueKind.Null);
  }

  [Test]
  [Arguments(null)]
  [Arguments("null")]
  [Arguments("{}")]
  public async Task Rederive_NoStoredScope_IsOnTheNoTenantLedgerAsync(string? scope) {
    var message = _rebuild().Rederive(_stored(typeof(RebuildOrderPlaced), scope: scope))!;

    await Assert.That(message.StreamId).IsEqualTo(AuditLedger.StreamFor(null));
    await Assert.That(_record(message).GetProperty("TenantId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(message.Scope).IsNull();
  }

  [Test]
  public async Task Rederive_AScopeWithOnlyAUser_KeepsTheUserAsync() {
    var message = _rebuild().Rederive(_stored(typeof(RebuildOrderPlaced), scope: """{"u":"user-1","t":7}"""))!;

    await Assert.That(_record(message).GetProperty("UserId").GetString()).IsEqualTo("user-1");
    await Assert.That(_record(message).GetProperty("TenantId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(_record(message).GetProperty("Scope").TryGetProperty("TenantId", out _)).IsFalse();
  }

  #endregion

  #region Deriving again

  [Test]
  public async Task Rederive_TwiceForOneEvent_IsTheSameRecordAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced));
    var rebuild = _rebuild();

    var first = _record(rebuild.Rederive(stored)!);
    var second = _record(rebuild.Rederive(stored)!);

    await Assert.That(second.GetRawText()).IsEqualTo(first.GetRawText())
      .Because("a record is a function of its event and scope, so deriving it again changes nothing");
  }

  [Test]
  public async Task Rederive_ARecordTheLiveWriterMinted_IsTheSameRecordAsync() {
    var stored = _stored(typeof(RebuildOrderPlaced));
    var live = AuditOutboxMessageBuilder.TryBuildAuditMessage(new OutboxMessage {
      MessageId = stored.EventId,
      Envelope = new Whizbang.Core.Observability.MessageEnvelope<JsonElement> {
        MessageId = new Whizbang.Core.ValueObjects.MessageId(stored.EventId),
        Payload = JsonDocument.Parse(stored.EventData).RootElement.Clone(),
        Hops = [],
      },
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata { MessageId = new Whizbang.Core.ValueObjects.MessageId(stored.EventId), Hops = [] },
      EnvelopeType = "MessageEnvelope",
      IsEvent = true,
      MessageType = stored.EventType,
    }, _options());

    var rebuilt = _rebuild().Rederive(stored)!;

    await Assert.That(_record(rebuilt).GetProperty("Id").GetGuid()).IsEqualTo(_record(live!).GetProperty("Id").GetGuid())
      .Because("a re-derived record deduplicates against the one the event produced when it was first written");
  }

  private static SystemEventOptions _options() {
    var options = new SystemEventOptions();
    options.EnableEventAudit();
    return options;
  }

  #endregion

  #region Running a rebuild

  private sealed class RecordingChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> Queued { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      Queued.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() => [.. Queued];

    public bool HasPending => Queued.Count > 0;
  }

  /// <summary>A stored history served in keyset pages, as the coordinator's selection serves it.</summary>
  private sealed class StoredHistory(IReadOnlyList<RedeliveryEvent> events) {
    public List<RedeliveryRequest> Requests { get; } = [];

    public Task<IReadOnlyList<RedeliveryEvent>> SelectAsync(RedeliveryRequest request, CancellationToken cancellationToken) {
      Requests.Add(request);
      IReadOnlyList<RedeliveryEvent> page = [.. events
        .Where(e => request.AfterStreamId is null
          || e.StreamId.CompareTo(request.AfterStreamId.Value) > 0
          || (e.StreamId == request.AfterStreamId.Value && e.Version > request.AfterVersion))
        .Take(request.MaxEvents)];
      return Task.FromResult(page);
    }
  }

  private static IReadOnlyList<RedeliveryEvent> _history() => [
    _stored(typeof(RebuildOrderPlaced), version: 1),
    _stored(typeof(RebuildHeartbeat), version: 2),
    _stored(typeof(RebuildPaymentTaken), version: 3),
    _stored(typeof(RebuildPresence), flags: EventFlags.Ephemeral, version: 4),
    _stored(typeof(RebuildBodyKept), version: 5),
  ];

  [Test]
  public async Task RunAsync_SelectsEveryStoredEvent_AndQueuesOnlyTheEligibleAsync() {
    var history = new StoredHistory(_history());
    var channel = new RecordingChannel();

    var result = await _rebuild().RunAsync(history.SelectAsync, channel, new RequestAuditRebuildCommand(), maxEvents: 100, pageSize: 2);

    await Assert.That(result.Selected).IsEqualTo(5);
    await Assert.That(result.Rederived).IsEqualTo(3);
    await Assert.That(channel.Queued.Select(m => _record(m).GetProperty("OriginalStreamPosition").GetInt64()))
      .IsEquivalentTo(new long[] { 1, 3, 5 })
      .Because("the heartbeat is excluded and the presence is ephemeral; the other three are audited");
  }

  [Test]
  public async Task RunAsync_Twice_YieldsOneRecordPerOriginEventAsync() {
    var history = new StoredHistory(_history());
    var channel = new RecordingChannel();
    var rebuild = _rebuild();

    await rebuild.RunAsync(history.SelectAsync, channel, new RequestAuditRebuildCommand(), maxEvents: 100, pageSize: 2);
    await rebuild.RunAsync(history.SelectAsync, channel, new RequestAuditRebuildCommand(), maxEvents: 100, pageSize: 2);

    var recordIds = channel.Queued.Select(m => _record(m).GetProperty("Id").GetGuid()).Distinct().ToList();
    await Assert.That(channel.Queued).Count().IsEqualTo(6);
    await Assert.That(recordIds).Count().IsEqualTo(3)
      .Because("the audit perspective keys a record by its id, so six deliveries of three records are three rows");
  }

  [Test]
  public async Task RunAsync_PagesWithTheKeysetOfTheLastEventAsync() {
    var history = new StoredHistory(_history());

    await _rebuild().RunAsync(history.SelectAsync, new RecordingChannel(), new RequestAuditRebuildCommand(), maxEvents: 100, pageSize: 2);

    await Assert.That(history.Requests).Count().IsEqualTo(4)
      .Because("three pages of at most two, then the empty page that ends the walk");
    await Assert.That(history.Requests[0].AfterStreamId).IsNull();
    await Assert.That(history.Requests[1].AfterVersion).IsEqualTo(2L);
    await Assert.That(history.Requests[2].AfterVersion).IsEqualTo(4L);
  }

  [Test]
  public async Task RunAsync_StopsAtTheEventCapAsync() {
    var history = new StoredHistory(_history());

    var result = await _rebuild().RunAsync(history.SelectAsync, new RecordingChannel(), new RequestAuditRebuildCommand(), maxEvents: 3, pageSize: 2);

    await Assert.That(result.Selected).IsEqualTo(3);
    await Assert.That(history.Requests[1].MaxEvents).IsEqualTo(1)
      .Because("the last page asks only for what is left under the cap");
  }

  [Test]
  [Arguments(2, 2)]
  [Arguments(50, 3)]
  public async Task RunAsync_ARequestedCap_IsClampedByTheOriginsCapAsync(int requested, int expectedSelected) {
    var history = new StoredHistory(_history());

    var result = await _rebuild().RunAsync(history.SelectAsync, new RecordingChannel(),
      new RequestAuditRebuildCommand { MaxEvents = requested }, maxEvents: 3, pageSize: 10);

    await Assert.That(result.Selected).IsEqualTo(expectedSelected)
      .Because("a requester may ask for less than the origin's cap, never more");
  }

  [Test]
  public async Task RequestAuditRebuildCommand_IsAuditedWithItsStreamCountAsync() {
    var command = new RequestAuditRebuildCommand { StreamIds = [Guid.NewGuid()] };

    await Assert.That(((Whizbang.Core.Audit.IBoundedCommandAudit)command).ListsAuditedAsLengths())
      .IsEquivalentTo(new[] { nameof(RequestAuditRebuildCommand.StreamIds) })
      .Because("a rebuild can name any number of streams, and its own audit record keeps how many, not every id");
  }

  [Test]
  public async Task RunAsync_PassesTheSelectionThroughAsync() {
    var history = new StoredHistory([]);
    var stream = Guid.NewGuid();

    await _rebuild().RunAsync(history.SelectAsync, new RecordingChannel(), new RequestAuditRebuildCommand {
      TenantScope = "tenant-a",
      StreamIds = [stream],
      FromCommitSequence = 10,
      ToCommitSequence = 20,
    }, maxEvents: 100, pageSize: 50);

    var request = history.Requests.Single();
    await Assert.That(request.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(request.StreamIds!).IsEquivalentTo(new[] { stream });
    await Assert.That(request.FromCommitSequence).IsEqualTo(10L);
    await Assert.That(request.ToCommitSequence).IsEqualTo(20L);
    await Assert.That(request.EventTypes).IsNull()
      .Because("eligibility is decided per type after selection; OptOut admits types no list could name in advance");
    await Assert.That(request.MaxEvents).IsEqualTo(50);
  }

  [Test]
  public async Task RunAsync_WithEventAuditOff_SelectsNothingAsync() {
    var history = new StoredHistory(_history());
    var rebuild = new AuditLedgerRebuild(new Catalog(), new SystemEventOptions(), new FakeTimeProvider(_now));

    var result = await rebuild.RunAsync(history.SelectAsync, new RecordingChannel(), new RequestAuditRebuildCommand(), maxEvents: 100, pageSize: 2);

    await Assert.That(result.Selected).IsEqualTo(0);
    await Assert.That(history.Requests).IsEmpty()
      .Because("a service that writes no audit records has no ledger to rebuild, and reading its whole store would be for nothing");
  }

  #endregion
}
