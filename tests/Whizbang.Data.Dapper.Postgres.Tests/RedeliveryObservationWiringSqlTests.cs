using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// End-to-end lock on the seam between the store's redelivery projection and the parsed observation
/// the poison detector consumes: real SQL against a real database, parsed by the real parser.
/// </summary>
/// <remarks>
/// <para>
/// This seam is where a fail-open regression shipped. The projection was extended to carry the inbox
/// attempt count, and the detector was changed to require it as evidence before quarantining — but
/// the parser was never taught to read it, so the value was always null. The detector's guard is
/// <c>ProcessingAttempts is { } attempts &amp;&amp; attempts &gt; 0</c>; a null fails that pattern,
/// which made the entire observation-count bound unreachable. A safety mechanism silently became a
/// no-op.
/// </para>
/// <para>
/// It survived review because it is invisible from every angle that was being looked at. A disabled
/// detector and a correctly-gated detector both emit zero quarantines, so no amount of runtime
/// observation distinguishes them — a live check reported "zero new quarantines" and that reads
/// exactly like success. And every unit test for the feature CONSTRUCTS the observation by hand
/// (<c>new InboxRedeliveryObservation(id, 10) { ProcessingAttempts = 10 }</c>), which exercises the
/// consumer while proving nothing about the producer.
/// </para>
/// <para>
/// Hence this test: no hand-built DTO anywhere. Rows go into the real tables, the real
/// <see cref="InboxRedeliveryObservationSql.ObservationQuery"/> runs against them, and the real
/// <see cref="InboxRedeliveryObservation.ParseProjection"/> reads the result. If any link in that
/// chain stops carrying the attempt count, this fails.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
public class RedeliveryObservationWiringSqlTests : PostgresTestBase {

  private const string INSERT_INBOX = @"
    INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, attempts)
    VALUES (@MessageId, 'TestHandler', 'TestMessage', '{}'::jsonb, '{}'::jsonb, @Attempts)";

  private const string INSERT_DEDUP = @"
    INSERT INTO wh_message_deduplication (message_id, observation_count)
    VALUES (@MessageId, @ObservationCount)";

  [Test]
  public async Task ProjectionCarriesTheInboxAttemptCount_AllTheWayToTheParsedObservationAsync() {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var messageId = Guid.NewGuid();

    await connection.ExecuteAsync(INSERT_INBOX, new { MessageId = messageId, Attempts = 7 });
    await connection.ExecuteAsync(INSERT_DEDUP, new { MessageId = messageId, ObservationCount = 12 });

    var json = await connection.ExecuteScalarAsync<string>(
      InboxRedeliveryObservationSql.ObservationQuery(""),
      new { observedIds = new[] { messageId } });

    var observations = InboxRedeliveryObservation.ParseProjection(json);

    await Assert.That(observations.Count).IsEqualTo(1)
      .Because("an observation count above one is a redelivery and must be reported");

    await Assert.That(observations[0].ObservationCount).IsEqualTo(12);
    await Assert.That(observations[0].ProcessingAttempts).IsEqualTo(7)
      .Because("the attempt count is the EVIDENCE the poison bound requires before quarantining. If "
             + "it arrives null the detector's guard cannot match and the entire observation-count "
             + "bound becomes unreachable — a safety mechanism turned into a silent no-op, which is "
             + "indistinguishable from working correctly by any external measure");
  }

  [Test]
  public async Task NoInboxRow_YieldsNullAttempts_NotZeroAsync() {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var messageId = Guid.NewGuid();

    // Dedup row with NO matching inbox row: already processed and removed, or never stored. The
    // projection LEFT JOINs, so this is the null case.
    await connection.ExecuteAsync(INSERT_DEDUP, new { MessageId = messageId, ObservationCount = 12 });

    var json = await connection.ExecuteScalarAsync<string>(
      InboxRedeliveryObservationSql.ObservationQuery(""),
      new { observedIds = new[] { messageId } });

    var observations = InboxRedeliveryObservation.ParseProjection(json);

    await Assert.That(observations.Count).IsEqualTo(1);
    await Assert.That(observations[0].ProcessingAttempts).IsNull()
      .Because("no inbox row means UNMEASURED, and the detector deliberately treats absence as "
             + "insufficient evidence. Collapsing it to zero would be a fabricated reading that "
             + "quarantine decisions then act on");
  }

  [Test]
  public async Task FirstSighting_IsNotReportedAsARedeliveryAsync() {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var messageId = Guid.NewGuid();

    await connection.ExecuteAsync(INSERT_INBOX, new { MessageId = messageId, Attempts = 1 });
    await connection.ExecuteAsync(INSERT_DEDUP, new { MessageId = messageId, ObservationCount = 1 });

    var json = await connection.ExecuteScalarAsync<string>(
      InboxRedeliveryObservationSql.ObservationQuery(""),
      new { observedIds = new[] { messageId } });

    await Assert.That(InboxRedeliveryObservation.ParseProjection(json).Count).IsEqualTo(0)
      .Because("one delivery is a first sighting, not a redelivery — carrying an attempt count must "
             + "not smuggle it past the count > 1 filter");
  }
}
