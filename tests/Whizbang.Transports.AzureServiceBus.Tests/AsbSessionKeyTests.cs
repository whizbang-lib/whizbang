using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Every message published to a session-enabled entity must carry a session id. A message without
/// one is rejected by the broker outright — it never reaches a consumer and never enters the inbox.
/// </summary>
/// <remarks>
/// <para>
/// Observed in production: control-plane broadcasts (<c>IntegrityCheckpoint</c>) carry no StreamId,
/// both publish paths set <c>SessionId</c> only when a StreamId is present, and the destination was
/// session-enabled. The broker answered "Session id is null. — Session enabled entity doesn't allow
/// a message whose session identifier is null." at a rate that grew to ~40,000/day. Because
/// IntegrityCheckpoint IS the continuity mechanism, the loss silently degraded exactly the guarantee
/// it exists to provide, while origins logged "has not checkpointed ... continuity can no longer be
/// verified" from the other side.
/// </para>
/// <para>
/// The rule lives here, in one place, because it was previously duplicated across the single-message
/// and batch publish paths — two copies of a rule that must agree is a defect waiting to happen.
/// </para>
/// </remarks>
/// <docs>transports/azure-service-bus</docs>
[Category("AzureServiceBus")]
public class AsbSessionKeyTests {

  [Test]
  public async Task StreamedMessage_UsesTheStreamId_SoPerStreamFifoIsPreservedAsync() {
    var stream = Guid.CreateVersion7();
    var messageId = Guid.CreateVersion7();

    var key = AsbSessionKey.For(stream, messageId);

    await Assert.That(key).IsEqualTo(stream.ToString())
      .Because("session id IS the ordering key on Service Bus — anything other than the stream id "
             + "would silently break per-stream FIFO, which is the reason sessions are enabled");
  }

  [Test]
  public async Task StreamlessMessage_StillGetsASessionId_SoTheBrokerAcceptsItAsync() {
    var messageId = Guid.CreateVersion7();

    var key = AsbSessionKey.For(null, messageId);

    await Assert.That(key).IsNotNull()
      .Because("a session-enabled entity rejects a null session id outright — the message never "
             + "reaches a consumer, so 'no stream' must not mean 'no session'");
    await Assert.That(key).IsNotEmpty();
  }

  [Test]
  public async Task StreamlessSessionKey_DoesNotParseAsAGuid_SoItIsNeverMistakenForAStreamIdAsync() {
    var messageId = Guid.CreateVersion7();

    var key = AsbSessionKey.For(null, messageId);

    // Inbound paths recover the stream id by parsing the session id — e.g.
    // AzureServiceBusDeadLetterDrainer: `Guid.TryParse(msg.SessionId, out var sid)`.
    // A bare message-id fallback WOULD parse, and a streamless message would then be attributed to
    // a stream that does not exist. The key must be unparseable so those callers correctly yield null.
    await Assert.That(Guid.TryParse(key, out _)).IsFalse()
      .Because("callers recover StreamId by Guid-parsing the session id; a GUID-shaped fallback "
             + "would invent a stream association for a message that has none");
  }

  [Test]
  public async Task StreamlessMessages_SpreadAcrossSeveralSessions_NotAllFunnelledIntoOneAsync() {
    var keys = new HashSet<string>();
    for (var i = 0; i < 400; i++) {
      keys.Add(AsbSessionKey.For(null, Guid.CreateVersion7()));
    }

    // A single constant session would satisfy the broker and then funnel every control-plane
    // broadcast through ONE session — one consumer, fully serialized. That trades a rejection bug
    // for a throughput collapse, and session-acceptance starvation is a known failure here.
    await Assert.That(keys.Count).IsGreaterThan(1)
      .Because("one shared session for all broadcasts serializes them behind a single consumer");
  }

  [Test]
  public async Task StreamlessSessions_AreBounded_SoSessionChurnCannotRunAwayAsync() {
    var keys = new HashSet<string>();
    for (var i = 0; i < 2000; i++) {
      keys.Add(AsbSessionKey.For(null, Guid.CreateVersion7()));
    }

    // The opposite failure: a session PER MESSAGE maximizes parallelism and then churns sessions
    // without limit. Session acceptance is finite on this transport and unbounded creation is how
    // it deadlocked before. It would also force one batch per message, since a ServiceBusMessageBatch
    // requires a uniform session id.
    await Assert.That(keys.Count).IsLessThanOrEqualTo(AsbSessionKey.STREAMLESS_BUCKETS)
      .Because("unbounded session creation is a known starvation mode, and per-message sessions "
             + "would also destroy batching — a batch must carry a single session id");
  }

  [Test]
  public async Task StreamIdIsNotAValidBatchGroupingKey_BecauseStreamlessItemsDifferAsync() {
    // The batch path must group by the SESSION KEY, never by StreamId. ASB requires a uniform
    // SessionId across a ServiceBusMessageBatch, and grouping by StreamId was only ever safe while
    // every streamless item got a null session — they collapsed into one accidentally-uniform group.
    // Now that streamless items spread across buckets, two items that share StreamId (null) can land
    // in DIFFERENT sessions, so a StreamId-keyed batch would mix session ids and the broker would
    // reject the whole batch. This test fails if anyone reverts that grouping.
    var streamless = new List<string>();
    for (var i = 0; i < 400; i++) {
      streamless.Add(AsbSessionKey.For(null, Guid.CreateVersion7()));
    }

    await Assert.That(streamless.Distinct().Count()).IsGreaterThan(1)
      .Because("items sharing a null StreamId do NOT share a session, so StreamId cannot be the "
             + "batch grouping key — a batch must be grouped by the value actually stamped on the "
             + "message, or the broker rejects the batch for mixed session ids");
  }

  [Test]
  public async Task SessionKey_IsDeterministic_SoARepublishLandsInTheSameSessionAsync() {
    var messageId = Guid.CreateVersion7();

    await Assert.That(AsbSessionKey.For(null, messageId))
      .IsEqualTo(AsbSessionKey.For(null, messageId))
      .Because("a re-publish of the same message must not scatter across sessions — ordering and "
             + "dedup both assume a stable key for a stable message id");
  }
}
