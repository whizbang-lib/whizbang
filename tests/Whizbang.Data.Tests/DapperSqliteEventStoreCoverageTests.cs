using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Sqlite;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Coverage for <see cref="DapperSqliteEventStore.ReadPolymorphicAsync"/> branches the existing
/// <c>DapperSqliteEventStoreDeepPathTests</c> / <c>DapperSqliteEventStorePolymorphicTests</c>
/// suites do not reach: skipping a row at the caller's fromEventId cursor, continuing past an
/// unregistered candidate event type to a later matching one, a MessageId type that cannot be
/// resolved from JsonOptions, a row missing its Payload property entirely, and a Hops-list type
/// that cannot be resolved from JsonOptions. An event store's appends must be durable and its
/// reads must return exactly what was appended, in order -- this is the same contract
/// Whizbang.Data.Postgres implements, so divergence between the two stores on any of these edge
/// cases is the bug worth catching.
/// </summary>
public class DapperSqliteEventStoreCoverageTests : IDisposable {
  private DapperTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public void Cleanup() {
    _testBase?.Cleanup();
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  // ========================================
  // fromEventId FILTER: SKIP AT THE CURSOR
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_FromEventIdEqualToTheRow_SkipsTheRowAsync() {
    // An event store's reads must return exactly what was appended, in order, and never
    // redeliver a row the caller has already consumed. If this filter regressed, a caller
    // resuming from its last-seen event id would see that same row again on every subsequent
    // poll instead of only strictly-newer ones -- turning an at-least-once catch-up read into an
    // infinite replay of the same event.
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - fromEventId equal to the row's own id means "already delivered", so it is filtered
    // before the row is ever matched against a candidate event type.
    var events = await _readPolymorphicAsync(eventStore, streamId, rowEventId, [typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(0)
      .Because("a row at or before the caller's cursor must never be redelivered");
  }

  // ========================================
  // CANDIDATE TYPE LOOP: SKIP AN UNREGISTERED TYPE, KEEP GOING
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_FirstEventTypeUnregistered_ContinuesToTheNextTypeAndMatchesAsync() {
    // ReadPolymorphicAsync takes a caller-supplied list of candidate event types to reconstruct
    // a polymorphic union. If hitting an unregistered candidate type ended the loop instead of
    // skipping it, a real event of a type listed AFTER an unrelated, unregistered type would
    // never be recognized -- turning a working subscriber into one that silently misses every
    // payload shape ordered after a type it does not know about.
    var eventStore = _createLegacyFormatEventStore();
    var streamId = Guid.NewGuid();
    // TrackedGuid.NewMedo(), not Guid.NewGuid() or Guid.CreateVersion7(): unlike the
    // fromEventId-skip and missing-Payload tests, TestEvent (the second candidate) actually
    // matches here, so this row's MessageId is fully deserialized through the legacy converter
    // -- and that path round-trips the id through TrackedGuid, which is what the working
    // legacy-format tests in DapperSqliteEventStoreDeepPathTests seed with.
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"continues-past-unregistered"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - UnregisteredCandidateType resolves to no JsonTypeInfo and must be skipped rather
    // than treated as fatal; TestEvent, listed second, must still be tried and matched.
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(UnregisteredCandidateType), typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(1)
      .Because("an unregistered candidate type must not stop the loop before a later, matching type is tried");
    await Assert.That(((TestEvent)events[0].Payload).Payload).IsEqualTo("continues-past-unregistered");
  }

  // ========================================
  // MessageId TYPE UNRESOLVABLE: SKIP THE ROW, NOT THE WHOLE READ
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_MessageIdTypeUnresolvable_SkipsTheRowAsync() {
    // FINDING, not an endorsed invariant: _tryDeserializeMessageId's "if (messageIdTypeInfo ==
    // null) return null;" (DapperSqliteEventStore.cs) means a MessageId resolver misconfiguration
    // silently drops the event from ReadPolymorphicAsync's results -- no exception, no log, no
    // trace. Unlike the Hops-list case (optional, best-effort trace metadata by the method's own
    // doc comment), MessageId is the event's own identity, not something safe to degrade quietly.
    // A consumer reading the stream would just see fewer events than were appended, with nothing
    // to say why. This test pins the CURRENT behavior for coverage purposes; it is not asserting
    // that silent data loss here is correct, and the owner should decide whether this should
    // instead throw or log.
    // Same reasoning as the hops test: derive from the standard options so the ONLY thing that
    // differs from a working read is MessageId being unresolvable.
    var options = JsonOptionsHelper.CreateOptions();
    options.TypeInfoResolver = new ExcludingTypeInfoResolver(options.TypeInfoResolver!, typeof(MessageId));
    var eventStore = _createEventStore(options);
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"messageid-unresolvable"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - the Payload deserializes fine (TestEvent still resolves), but MessageId's own type
    // is unresolvable from this options instance.
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(0)
      .Because("current behavior: a row whose MessageId type cannot be resolved is silently "
             + "dropped rather than thrown from -- pinned here as an observed finding, not endorsed");
  }

  // ========================================
  // PAYLOAD PROPERTY MISSING: SKIP THE ROW
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_RowMissingPayloadProperty_IsSkippedAsync() {
    // Reads must return exactly what was appended. A row that somehow lacks a Payload property
    // entirely must be skipped, not synthesized into a fake event or allowed to crash the
    // enumeration for every other row in the stream.
    var eventStore = _createLegacyFormatEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(0)
      .Because("a row with no Payload property has nothing to reconstruct and must be skipped");
  }

  // ========================================
  // Hops LIST TYPE UNRESOLVABLE: DEGRADE TO EMPTY, DO NOT CRASH THE EVENT
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_HopsListTypeUnresolvable_ReturnsEmptyHopsAsync() {
    // Hops are diagnostic trace metadata, not authoritative event data. If a resolver gap for
    // the hops-list type ever crashed the read instead of degrading to an empty list, a hop-
    // serialization gap would take down event delivery entirely instead of merely losing trace
    // metadata for that one row.
    // Start from the standard options and swap only the resolver. Building a bare
    // JsonSerializerOptions instead would silently drop every converter and setting
    // JsonOptionsHelper installs, so the read would fail for reasons unrelated to hops.
    var options = JsonOptionsHelper.CreateOptions();
    options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
      new LegacyMessageIdResolver(),
      new ExcludingTypeInfoResolver(options.TypeInfoResolver!, typeof(List<MessageHop>)));
    var eventStore = _createEventStore(options);
    var streamId = Guid.NewGuid();
    // TrackedGuid.NewMedo(): this test needs MessageId to deserialize successfully (via the
    // legacy converter) so the read reaches _deserializeHops at all. A row whose MessageId does
    // not survive that round trip is dropped before the hops branch is ever reached, which reads
    // as "hops broke the event" when it did not.
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"hops-unresolvable"},"Hops":[]}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(1)
      .Because("an unresolvable hops-list type must not prevent the event itself from being returned");
    await Assert.That(events[0].Hops).Count().IsEqualTo(0)
      .Because("hops must degrade to an empty list rather than propagate the resolver gap as an exception");
    await Assert.That(((TestEvent)events[0].Payload).Payload).IsEqualTo("hops-unresolvable");
  }

  // ========================================
  // HELPERS
  // ========================================

  private DapperSqliteEventStore _createEventStore() {
    return _createEventStore(JsonOptionsHelper.CreateOptions());
  }

  private DapperSqliteEventStore _createEventStore(JsonSerializerOptions jsonOptions) {
    return new DapperSqliteEventStore(
      _testBase.ConnectionFactory,
      _testBase.Executor,
      jsonOptions,
      new PolicyEngine());
  }

  private DapperSqliteEventStore _createLegacyFormatEventStore() {
    var options = JsonOptionsHelper.CreateOptions();
    options.TypeInfoResolver = JsonTypeInfoResolver.Combine(new LegacyMessageIdResolver(), options.TypeInfoResolver);
    return _createEventStore(options);
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readPolymorphicAsync(
    DapperSqliteEventStore eventStore, Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes) {
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, fromEventId, eventTypes)) {
      events.Add(evt);
    }
    return events;
  }

  private async Task _seedRawEnvelopeRowAsync(Guid streamId, long sequenceNumber, string envelopeJson) {
    // Insert through the same executor the store reads with, so stream_id is bound identically.
    // A raw SqliteCommand with streamId.ToString() stores a TEXT value that the store's
    // Guid-parameterized WHERE clause never matches -- the row lands in the table and is then
    // invisible to every read, so a test asserting "no events came back" passes without the
    // code under test ever having run.
    const string sql = @"
      INSERT INTO whizbang_event_store (stream_id, sequence_number, envelope, created_at)
      VALUES (@StreamId, @SequenceNumber, @Envelope, @CreatedAt)";
    await _testBase.Executor.ExecuteAsync(
      _testBase.Connection,
      sql,
      new {
        StreamId = streamId,
        SequenceNumber = sequenceNumber,
        Envelope = envelopeJson,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
      });
  }

  private sealed class TestFixture : DapperTestBase;

  /// <summary>A type deliberately absent from every JsonSerializerContext in the resolver
  /// chain, used to verify the candidate-type loop skips past it.</summary>
  private sealed class UnregisteredCandidateType;

  /// <summary>Wraps another resolver, forcing one specific type to resolve to no JsonTypeInfo
  /// while delegating every other type unchanged. Used to construct JsonSerializerOptions where
  /// exactly one otherwise-registered type is deliberately unresolvable.</summary>
  private sealed class ExcludingTypeInfoResolver(IJsonTypeInfoResolver inner, Type excludedType) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == excludedType ? null : inner.GetTypeInfo(type, options);
  }

  /// <summary>
  /// Test-only resolver that registers MessageId with the legacy object-form converter
  /// ({"Value":"guid"}), taking precedence over the string-form (Uuid7) converter in the
  /// standard chain -- the shape ReadPolymorphicAsync's MessageId extraction expects.
  /// </summary>
  private sealed class LegacyMessageIdResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(MessageId)) {
        return null;
      }
      return JsonMetadataServices.CreateValueInfo<MessageId>(options, new LegacyObjectFormMessageIdConverter());
    }
  }

  /// <summary>
  /// Reads and writes MessageId in the legacy object form {"Value":"guid"} used by the
  /// polymorphic reader's MessageId extraction.
  /// </summary>
  private sealed class LegacyObjectFormMessageIdConverter : JsonConverter<MessageId> {
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
      using var doc = JsonDocument.ParseValue(ref reader);
      return MessageId.From(doc.RootElement.GetProperty("Value").GetGuid());
    }

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) {
      writer.WriteStartObject();
      writer.WriteString("Value", value.Value);
      writer.WriteEndObject();
    }
  }
}
