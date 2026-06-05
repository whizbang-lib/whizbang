using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 8 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks
/// the end-to-end preservation contract for every DLQ promotion through
/// <c>move_to_dead_letters</c>:
///
/// <list type="number">
/// <item><description>Full exception text reaches
/// <c>wh_dead_letters.error_text</c> with no truncation or transformation —
/// the column is TEXT (uncapped) and the SQL function passes
/// <c>p_error_text</c> through verbatim.</description></item>
/// <item><description><c>error_fingerprint</c> matches what
/// <c>compute_dead_letter_fingerprint(error_text)</c> would return for the
/// stored text — the round-trip integrity invariant. The SQL function is
/// IMMUTABLE; same input MUST produce same output. Anything else means
/// either (a) the move function bypassed the fingerprint write or (b) a
/// row was manually inserted without going through move_to_dead_letters
/// (which Slice 3a + Slice 6's aggregator would also catch since they share
/// the algorithm function).</description></item>
/// </list>
///
/// <para>This slice is the safety net for the dual-hash design: any future
/// drift between the capture-time fingerprint and the SQL function's current
/// output (e.g. someone changes the algorithm body without bumping the version
/// or running aggregate_dead_letters to backfill) shows up as a non-zero row
/// count on the round-trip integrity query.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class DlqStackTracePreservationSqlTests : EFCoreTestBase {

  // Realistic multi-line stacks across the three source-table failure modes
  // (outbox/inbox/perspective). Each has a distinct exception type and a
  // distinct first in-app frame so they produce distinct fingerprints.
  private const string _outboxStack = """
    System.InvalidOperationException: Could not open connection to 'jdx_bff' (publish path)
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
       at Whizbang.Core.Workers.OutboxDrainWorker.InvokeOutboxLifecycleStageAsync()
       at Whizbang.Core.Workers.OutboxDrainWorker.ExecuteAsync(CancellationToken ct)
       at System.Threading.Tasks.Task.RunContinuations()
    """;

  private const string _inboxStack = """
    System.NullReferenceException: Object reference not set to an instance of an object
       at Whizbang.Data.EFCore.Postgres.Functions.InboxDispatch.ClaimAsync(Guid instanceId)
       at Whizbang.Core.Workers.InboxDispatchWorker.InvokeInboxLifecycleStageAsync()
       at Whizbang.Core.Workers.InboxDispatchWorker.ExecuteAsync(CancellationToken ct)
    """;

  private const string _perspectiveStack = """
    System.OperationCanceledException: The operation was canceled.
       at a consumer.Perspectives.MyView.ApplyAsync(MessageEnvelope envelope)
       at Whizbang.Core.Workers.PerspectiveWorker.ProcessEventAsync()
       at System.Threading.Tasks.ValueTask.GetResult()
    """;

  // --- helpers ---

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = (NpgsqlConnection)CreateDbContext().Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _seedOutboxAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox (
        message_id, stream_id, destination, message_type, envelope_type,
        event_data, metadata, status, attempts, partition_number, is_event
      ) VALUES (
        @id, @stream, 'test-topic', 'TestMessage', 'MessageEnvelope',
        '{}'::jsonb, '{}'::jsonb, 1, 11, 0, false
      )
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedInboxAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_inbox (
        message_id, handler_name, message_type, event_data, metadata,
        status, attempts, received_at, partition_number
      ) VALUES (
        @id, 'TestHandler', 'TestMessage', '{}'::jsonb, '{}'::jsonb,
        1, 11, NOW(), 0
      )
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedPerspectiveAsync(NpgsqlConnection conn, Guid eventWorkId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_events (
        event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts
      ) VALUES (
        @id, @stream, 'TestPerspective', @event_id, 0, 1, 11
      )
      """;
    cmd.Parameters.AddWithValue("id", eventWorkId);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("event_id", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _moveAsync(NpgsqlConnection conn, string sourceTable, Guid sourceId, string errorText) {
    var deadLetterId = (Guid)TrackedGuid.NewMedo();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT move_to_dead_letters(@dlq, @src, @id, 99, @err, @inst, 'test-gen')";
    cmd.Parameters.AddWithValue("dlq", deadLetterId);
    cmd.Parameters.AddWithValue("src", sourceTable);
    cmd.Parameters.AddWithValue("id", sourceId);
    cmd.Parameters.AddWithValue("err", errorText);
    cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteScalarAsync();
    return deadLetterId;
  }

  // --- tests ---

  [Test]
  public async Task MoveToDeadLetters_PreservesFullStackTextAcrossAllSourceTablesAsync() {
    await using var conn = await _openAsync();
    var outboxMsg = (Guid)TrackedGuid.NewMedo();
    var inboxMsg = (Guid)TrackedGuid.NewMedo();
    var perspectiveWork = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, outboxMsg);
    await _seedInboxAsync(conn, inboxMsg);
    await _seedPerspectiveAsync(conn, perspectiveWork);

    var dlqOutbox = await _moveAsync(conn, "wh_outbox", outboxMsg, _outboxStack);
    var dlqInbox = await _moveAsync(conn, "wh_inbox", inboxMsg, _inboxStack);
    var dlqPerspective = await _moveAsync(conn, "wh_perspective_events", perspectiveWork, _perspectiveStack);

    var stored = new[] {
      (dlqOutbox, _outboxStack, "Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync"),
      (dlqInbox, _inboxStack, "Whizbang.Data.EFCore.Postgres.Functions.InboxDispatch.ClaimAsync"),
      (dlqPerspective, _perspectiveStack, "a consumer.Perspectives.MyView.ApplyAsync"),
    };

    foreach (var (dlqId, expectedFullText, signatureFrame) in stored) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT error_text FROM wh_dead_letters WHERE dead_letter_id = @id";
      cmd.Parameters.AddWithValue("id", dlqId);
      var storedText = (string?)await cmd.ExecuteScalarAsync();

      await Assert.That(storedText).IsNotNull();
      await Assert.That(storedText).IsEqualTo(expectedFullText)
        .Because("error_text MUST be preserved verbatim — TEXT column has no cap; any truncation here would lose the stack trace operators need for triage AND would invalidate the round-trip fingerprint check below.");
      await Assert.That(storedText!).Contains(signatureFrame)
        .Because("Distinctive frame from the throw site must survive — locks against any future SQL function that strips/normalizes the input text before storage.");
    }
  }

  [Test]
  public async Task MoveToDeadLetters_RoundTripFingerprintIntegrity_AcrossAllSourceTablesAsync() {
    await using var conn = await _openAsync();
    var outboxMsg = (Guid)TrackedGuid.NewMedo();
    var inboxMsg = (Guid)TrackedGuid.NewMedo();
    var perspectiveWork = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, outboxMsg);
    await _seedInboxAsync(conn, inboxMsg);
    await _seedPerspectiveAsync(conn, perspectiveWork);

    await _moveAsync(conn, "wh_outbox", outboxMsg, _outboxStack);
    await _moveAsync(conn, "wh_inbox", inboxMsg, _inboxStack);
    await _moveAsync(conn, "wh_perspective_events", perspectiveWork, _perspectiveStack);

    // The verification query from the plan: every row with a non-NULL fingerprint
    // must satisfy stored_fingerprint == compute_dead_letter_fingerprint(error_text).
    // Zero violations means the dual-hash contract holds end-to-end.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COUNT(*) FROM wh_dead_letters
      WHERE error_fingerprint IS NOT NULL
        AND error_fingerprint != compute_dead_letter_fingerprint(error_text)
      """;
    var violations = (long)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(violations).IsEqualTo(0L)
      .Because("Round-trip integrity: every stored fingerprint MUST match a fresh recomputation from the stored text. A non-zero count means either (a) move_to_dead_letters wrote a fingerprint inconsistent with its input text, or (b) someone INSERTed into wh_dead_letters bypassing the auto-population — either path silently breaks Slice 6's aggregation since the cluster key would diverge from the algorithm.");
  }

  [Test]
  public async Task MoveToDeadLetters_AllSourcesProduceDistinctFingerprintsAsync() {
    await using var conn = await _openAsync();
    var outboxMsg = (Guid)TrackedGuid.NewMedo();
    var inboxMsg = (Guid)TrackedGuid.NewMedo();
    var perspectiveWork = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, outboxMsg);
    await _seedInboxAsync(conn, inboxMsg);
    await _seedPerspectiveAsync(conn, perspectiveWork);

    var dlqOutbox = await _moveAsync(conn, "wh_outbox", outboxMsg, _outboxStack);
    var dlqInbox = await _moveAsync(conn, "wh_inbox", inboxMsg, _inboxStack);
    var dlqPerspective = await _moveAsync(conn, "wh_perspective_events", perspectiveWork, _perspectiveStack);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COUNT(DISTINCT error_fingerprint)
      FROM wh_dead_letters
      WHERE dead_letter_id IN (@outbox, @inbox, @perspective)
      """;
    cmd.Parameters.AddWithValue("outbox", dlqOutbox);
    cmd.Parameters.AddWithValue("inbox", dlqInbox);
    cmd.Parameters.AddWithValue("perspective", dlqPerspective);
    var distinctCount = (long)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(distinctCount).IsEqualTo(3L)
      .Because("Three distinct stacks → three distinct fingerprints. If this collapses to fewer, the fingerprint algorithm is mistakenly merging unrelated failure modes — operators would see fewer clusters than reality.");
  }
}
