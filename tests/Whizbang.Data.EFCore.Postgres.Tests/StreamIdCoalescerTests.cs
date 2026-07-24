using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 slice 3 invariants around <see cref="StreamIdCoalescer"/> — the
/// helper that turns the <c>List&lt;WorkBatchRow&gt;</c> returned by <c>claim_work</c>
/// into the three stream_id lists consumed by the drain channels.
/// </summary>
/// <remarks>
/// <para>
/// production forensic root cause (Jun 2026): rows with <c>stream_id = Guid.Empty</c>
/// (real zero-UUID, not NULL) were silently dropped by the pre-v0.657
/// <c>r.StreamId ?? r.WorkId ?? Guid.Empty</c> coalesce, because <c>??</c> only
/// catches null and the subsequent <c>.Where(g =&gt; g != Guid.Empty)</c> filter
/// discarded the Empty result. ClaimWorker bumped <c>attempts</c> every cycle but
/// never wrote to the drain channel — silently stuck forever.
/// </para>
/// <para>
/// This test class locks the consumer-side recovery contract: Empty MUST coalesce
/// to <c>WorkId</c> (the singleton-stream identity), MUST emit a Warning identifying
/// the offending row, and MUST appear in the resulting stream_id list. The fix is
/// unconditional — independent of <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy"/>
/// — because the only alternative is "leave the row stuck forever," which is the bug.
/// </para>
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public class StreamIdCoalescerTests {

  private sealed record _LogEntry(LogLevel Level, string Message);

  private sealed class _CapturingLogger : ILogger {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception)));
    }
    private sealed class _NullScope : IDisposable {
      public static readonly _NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// The production case: outbox row with Empty stream_id MUST reach OutboxStreamIds
  /// via the WorkId fallback, and the offending row MUST be named in a Warning so
  /// operators can hunt the producer.
  /// </summary>
  [Test]
  public async Task Coalesce_OutboxRowWithEmptyStreamId_FallsBackToWorkIdAndWarnsAsync() {
    Guid workId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() {
        Source = "outbox",
        WorkId = workId,
        StreamId = Guid.Empty,  // The production pattern
      }
    };
    var logger = new _CapturingLogger();

    var (_, outbox, _) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(outbox).Contains(workId)
      .Because("Empty stream_id MUST be treated the same as NULL — fall back to WorkId so the row reaches the drainer. Without this, production's stuck row stays stuck after v0.656's diagnostic-only deploy.");
    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("Exactly one Warning per offending row — silent recovery would mask the producer bug; producer-side fix is still needed.");
    await Assert.That(warnings[0].Message).Contains(workId.ToString())
      .Because("Operators reading the Warning need to identify which row this is. The MessageId is the only identifier carried at the claim_work projection level (message_type is NULL in that projection).");
    await Assert.That(warnings[0].Message).Contains("outbox")
      .Because("Multiple sources can produce Empty-stream rows; the Warning must name the source so operators triage the right table.");
  }

  /// <summary>
  /// Mirror invariant for inbox. Same shape — Empty→WorkId fallback + Warning.
  /// </summary>
  [Test]
  public async Task Coalesce_InboxRowWithEmptyStreamId_FallsBackToWorkIdAndWarnsAsync() {
    Guid workId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() {
        Source = "inbox",
        WorkId = workId,
        StreamId = Guid.Empty,
      }
    };
    var logger = new _CapturingLogger();

    var (_, _, inbox) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(inbox).Contains(workId)
      .Because("Inbox path has the same bug as outbox; same fix.");
    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1);
    await Assert.That(warnings[0].Message).Contains("inbox")
      .Because("Source must distinguish from outbox/perspective for operator triage.");
  }

  /// <summary>
  /// Perspective_stream path had a different shape (filtered on <c>HasValue</c>,
  /// not on Empty). v0.657 brings it into symmetry — Empty also coalesces.
  /// </summary>
  [Test]
  public async Task Coalesce_PerspectiveRowWithEmptyStreamId_FallsBackToWorkIdAndWarnsAsync() {
    Guid workId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() {
        Source = "perspective_stream",
        WorkId = workId,
        StreamId = Guid.Empty,
      }
    };
    var logger = new _CapturingLogger();

    var (perspective, _, _) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(perspective).Contains(workId)
      .Because("Perspective stream symmetry with outbox/inbox — Empty must NOT silently produce a Guid.Empty entry that the drainer can't act on.");
    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1);
    await Assert.That(warnings[0].Message).Contains("perspective")
      .Because("Source name in Warning so operators triage the right table.");
  }

  /// <summary>
  /// Negative case: NORMAL rows (non-Empty stream_id) MUST NOT trigger Warnings.
  /// The diagnostic budget is finite — production-healthy traffic must stay quiet.
  /// </summary>
  [Test]
  public async Task Coalesce_NormalRows_NoWarningsAsync() {
    Guid workId = TrackedGuid.NewMedo();
    Guid streamId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() {
        Source = "outbox",
        WorkId = workId,
        StreamId = streamId,
      }
    };
    var logger = new _CapturingLogger();

    var (_, outbox, _) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(outbox).Contains(streamId)
      .Because("Real stream_id rides through unchanged; the coalesce only kicks in for Empty/NULL.");
    await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty()
      .Because("Healthy traffic must produce zero Warning entries — otherwise operator dashboards become unreadable.");
  }

  /// <summary>
  /// NULL stream_id is the legitimate path for event-store-only / singleton-stream
  /// messages. Must coalesce to WorkId WITHOUT a Warning — NULL is not a bug.
  /// </summary>
  [Test]
  public async Task Coalesce_NullStreamId_FallsBackToWorkIdSilentlyAsync() {
    Guid workId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() {
        Source = "outbox",
        WorkId = workId,
        StreamId = null,
      }
    };
    var logger = new _CapturingLogger();

    var (_, outbox, _) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(outbox).Contains(workId)
      .Because("NULL stream_id is the documented singleton-stream marker — the coalesce uses WorkId, no diagnostic needed.");
    await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty()
      .Because("NULL is a legitimate value; Warning would create false-positive operator alerts.");
  }

  /// <summary>
  /// Defensive: a malformed row with NEITHER StreamId NOR WorkId can't drain.
  /// Skipping it is correct (nothing to fall back to) — but must not crash the
  /// whole claim batch, and must not log a Warning that names <c>Guid.Empty</c>
  /// (that's noise — there's no row identity to chase).
  /// </summary>
  [Test]
  public async Task Coalesce_RowMissingBothStreamIdAndWorkId_SkippedSilentlyAsync() {
    var rows = new List<WorkBatchRow> {
      new() { Source = "outbox", WorkId = null, StreamId = null },
      new() { Source = "outbox", WorkId = null, StreamId = Guid.Empty },
      new() { Source = "outbox", WorkId = Guid.Empty, StreamId = Guid.Empty },
    };
    var logger = new _CapturingLogger();

    var (_, outbox, _) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(outbox).IsEmpty()
      .Because("No identity to drain — neither StreamId nor WorkId is usable. The drainer would do nothing meaningful with these rows anyway.");
    await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty()
      .Because("Warning naming a Guid.Empty MessageId would be log noise — operators can't act on it. The storage-time Reject guard (slice 2) is the right place to catch this class of malformed row.");
  }

  /// <summary>
  /// Rows with an unrecognized <c>source</c> string (e.g. <c>receptor</c>) are
  /// passed through without contributing to any drain list. Forward-compat for
  /// future <c>claim_work</c> projections.
  /// </summary>
  [Test]
  public async Task Coalesce_UnknownSource_ProducesEmptyListsAsync() {
    Guid workId = TrackedGuid.NewMedo();
    Guid streamId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() { Source = "receptor", WorkId = workId, StreamId = streamId },
      new() { Source = "unknown_future_source", WorkId = workId, StreamId = streamId },
    };
    var logger = new _CapturingLogger();

    var (perspective, outbox, inbox) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(perspective).IsEmpty();
    await Assert.That(outbox).IsEmpty();
    await Assert.That(inbox).IsEmpty();
    await Assert.That(logger.Entries).IsEmpty()
      .Because("Unrecognized sources aren't an error — they're forward-compat. Logging would create false-positive operator alerts when claim_work adds a new source category.");
  }

  /// <summary>
  /// Defensive: passing <c>null</c> logger MUST NOT throw. Production wiring
  /// always supplies one, but library consumers may not.
  /// </summary>
  [Test]
  public async Task Coalesce_NullLogger_DoesNotThrowAsync() {
    Guid workId = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() { Source = "outbox", WorkId = workId, StreamId = Guid.Empty },
    };

    var (_, outbox, _) = StreamIdCoalescer.Coalesce(rows, logger: null);

    await Assert.That(outbox).Contains(workId)
      .Because("Coalesce contract: fallback path doesn't depend on logger presence. The Warning is best-effort.");
  }

  /// <summary>
  /// Multiple Empty-stream rows in the same batch produce multiple Warnings —
  /// one per row — so operators can count saturation, not just observe presence.
  /// </summary>
  [Test]
  public async Task Coalesce_MultipleEmptyStreamRows_OneWarningEachAsync() {
    Guid workId1 = TrackedGuid.NewMedo();
    Guid workId2 = TrackedGuid.NewMedo();
    Guid workId3 = TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      new() { Source = "outbox", WorkId = workId1, StreamId = Guid.Empty },
      new() { Source = "outbox", WorkId = workId2, StreamId = Guid.Empty },
      new() { Source = "inbox",  WorkId = workId3, StreamId = Guid.Empty },
    };
    var logger = new _CapturingLogger();

    var (_, outbox, inbox) = StreamIdCoalescer.Coalesce(rows, logger);

    await Assert.That(outbox).Contains(workId1);
    await Assert.That(outbox).Contains(workId2);
    await Assert.That(inbox).Contains(workId3);
    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(3)
      .Because("One Warning per row — operators graph the rate to know how aggressive a producer is, not just whether one row was bad.");
  }
}
