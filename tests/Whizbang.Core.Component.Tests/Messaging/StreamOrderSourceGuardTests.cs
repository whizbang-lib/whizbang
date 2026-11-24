// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2551: no worker or runner orders a stream by id. A message or event id is time-ordered and never a stream position:
/// each work item carries the position its read ranked it at (<c>InboxBatchRow.StreamPosition</c>, an envelope's
/// <c>LocalStreamVersion</c>), and the workers take that order (<c>StreamOrder</c>) or the order the read returned. This
/// reads the source of the workers, the runners, the messaging layer and the drivers, and fails on any sort or
/// comparison of ids, so a new site cannot bring the old rule back.
/// </summary>
/// <remarks>
/// The one fallback allowed is the replay's, for an event that carries neither a version nor a commit sequence, or an
/// origin's event with no origin position (#2379), shared with the receive order the SQL stores
/// (574_ReceivedStreamsAreVersionedInReceiveOrder.sql); it is allowed only in the files that define it, by exact text.
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
[Category("Messaging")]
public partial class StreamOrderSourceGuardTests {
  /// <summary>The trees read: every worker, runner, the messaging layer and every driver.</summary>
  private static readonly string[] _trees = [
    "src/Whizbang.Core/Workers", "src/Whizbang.Core/Perspectives", "src/Whizbang.Core/Messaging",
    "src/Whizbang.Data.Postgres", "src/Whizbang.Data.EFCore.Postgres", "src/Whizbang.Data.Dapper.Postgres",
    "src/Whizbang.Data.Dapper.Custom",
  ];

  /// <summary>Exact allowed texts, per file, with why each is the documented fallback and not a stream ordered by id.</summary>
  private static readonly (string File, string Text, string Reason)[] _allowed = [
    ("ReplayEventOrder.cs", "OrderBy(static e => e.MessageId.Value.ToString(\"D\"), StringComparer.Ordinal)",
      "the replay's fallback for a batch with an event that carries neither a version nor a commit sequence"),
    ("LastInReplayOrder.cs", "envelope.MessageId.Value.ToString(\"D\"), _byId.MessageId.Value.ToString(\"D\")",
      "the same fallback, computed without materializing the stream"),
    ("LastInReplayOrder.cs", "envelope.MessageId.Value.ToString(\"D\"), current.MessageId.Value.ToString(\"D\")",
      "the replay's tie-break within equal keys (ReplayEventOrder.TieBreak), never reached for versioned events"),
    ("InMemoryEventStore.cs", "(anchor is not null ? e.Version > anchor.Version : e.EventId.CompareTo(fromEventId.Value) > 0)",
      "an anchor the stream does not hold has no position in it; compared by id, as the Postgres stores read it (#2379)"),
    ("InMemoryEventStore.cs", "(after is not null ? e.Version > after.Version : e.EventId.CompareTo(afterEventId.Value) > 0)",
      "the same: a lower checkpoint the stream does not hold (#2381)"),
    ("InMemoryEventStore.cs", "(upTo is not null ? e.Version <= upTo.Version : e.EventId.CompareTo(upToEventId) <= 0)",
      "the same: an upper checkpoint the stream does not hold (#2381)"),
    ("CollectiveReplayApplier.cs", "allCollectives.OrderBy(static e => e.MessageId.Value.ToString(\"D\"), StringComparer.Ordinal)",
      "collectives of different streams share no stream position; an unstamped one is placed by id (#2449), the replay's fallback"),
    ("CollectiveReplayStart.cs", ": collective.MessageId.Value.CompareTo(after) > 0;",
      "a collective or a start with no commit sequence (unstamped) has no position across streams: the replay's fallback"),
    ("OutboxDrainWorker.cs", "List<OutboxBatchRow> rows = [.. rowsRaw.OrderBy(static r => r.MessageId)];",
      "publishing matches the outbox continuation, still an id cursor (#917, wh_continue_outbox_streams); a #2551 follow-up"),
    ("OutboxDrainWorker.cs", "(_, prior) => row.MessageId.CompareTo(prior) > 0 ? row.MessageId : prior);",
      "that continuation cursor: the largest id published; a #2551 follow-up"),
  ];

  // A sort keyed on an id, an id comparison, or the retired helper.
  [GeneratedRegex(@"\bOrderByMessageId\s*\(|\b(?:OrderBy|OrderByDescending|ThenBy|ThenByDescending|Sort)\s*\([^;]*?\b(?:MessageId|EventId)\b|\b(?:MessageId|EventId)(?:\.Value)?\.CompareTo\s*\(|\bstring\.CompareOrdinal\s*\([^;]*?\b(?:MessageId|EventId)\b")]
  private static partial Regex _idOrder();

  [Test]
  public async Task NoWorkerRunnerOrDriver_OrdersAStreamById_OutsideTheReplaysFallbackAsync() {
    var root = _findRepoRoot();
    var files = _trees.SelectMany(t => Directory.EnumerateFiles(Path.Combine(root, t), "*.cs", SearchOption.AllDirectories))
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .ToList();
    await Assert.That(files.Select(Path.GetFileName)).Contains("ReplayEventOrder.cs")
      .Because("precondition: the scan reads the runners, or it is not reading what it guards");
    await Assert.That(files.Select(Path.GetFileName)).Contains("InboxDrainWorker.cs");

    var findings = new List<(string File, string Text)>();
    foreach (var file in files) {
      findings.AddRange(_findings(Path.GetFileName(file), await File.ReadAllLinesAsync(file)));
    }

    var offenders = findings.Where(f => !_isAllowed(f)).Select(f => $"{f.File}: {f.Text}").ToList();
    var stale = _allowed.Where(a => !findings.Any(f => f.File == a.File && f.Text.Contains(a.Text, StringComparison.Ordinal)))
      .Select(a => $"{a.File}: {a.Text}").ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("these order a stream by id; take the position the work item carries (StreamOrder) or the order the read "
        + "returned: " + string.Join(" | ", offenders));
    await Assert.That(stale).IsEmpty()
      .Because("these allowances match nothing any more; remove them: " + string.Join(" | ", stale));
  }

  [Test]
  public async Task TheGuard_FlagsEachWayOfOrderingById_AndPassesAPositionOrderAndACommentAsync() {
    string[] probe = [
      "var a = rows.OrderByMessageId().ToList();",
      "var b = rows.OrderBy(r => r.MessageId).ToList();",
      "var c = events.OrderBy(e => e.LocalCommitSequence).ThenBy(e => e.MessageId.Value.ToString()).ToList();",
      "Array.Sort(array, static (x, y) => x.MessageId.CompareTo(y.MessageId));",
      "var d = x.EventId.CompareTo(y.EventId) < 0;",
      "var e = rows.OrderBy(r => r.StreamPosition).ToList();",
      "// rows.OrderByMessageId() is how the old rule read",
      "var f = events.OrderBy(e => e.LocalStreamVersion).ToList();",
    ];

    var flagged = _findings("Probe.cs", probe).Select(f => f.Text.Trim()).ToList();

    await Assert.That(flagged).IsEquivalentTo([probe[0], probe[1], probe[2], probe[3], probe[4]],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the retired helper, a sort keyed on an id, an id tie-break and an id comparison are each flagged; a sort "
        + "by stream position or version, and a comment, are not");
  }

  private static bool _isAllowed((string File, string Text) finding) =>
    _allowed.Any(a => a.File == finding.File && finding.Text.Contains(a.Text, StringComparison.Ordinal));

  private static IEnumerable<(string File, string Text)> _findings(string file, IReadOnlyList<string> lines) {
    foreach (var line in lines) {
      var trimmed = line.TrimStart();
      if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) {
        continue;
      }
      if (_idOrder().IsMatch(line)) {
        yield return (file, line);
      }
    }
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
