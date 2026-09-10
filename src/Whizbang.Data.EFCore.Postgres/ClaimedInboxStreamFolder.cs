using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Folds the inbox rows a claim returned into one entry per stream for the batch hooks (priority step 1): the
/// stream's most urgent row, its oldest arrival and how many of its rows the batch holds. Rows of other sources,
/// and rows a claim_work that predates the columns returns without them, fold to the standard band.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs</tests>
internal static class ClaimedInboxStreamFolder {
  private const string SOURCE_INBOX = "inbox";

  /// <summary>Folds <paramref name="rows"/> by stream, in the order each stream first appears (the claim's bucket order).</summary>
  internal static IReadOnlyList<InboxStreamFold> Fold(IReadOnlyList<WorkBatchRow> rows) {
    var order = new List<Guid>();
    var folds = new Dictionary<Guid, (int Priority, DateTimeOffset? Oldest, int Rows)>();
    foreach (var row in rows) {
      if (!string.Equals(row.Source, SOURCE_INBOX, StringComparison.Ordinal) || row.StreamId is not { } streamId || streamId == Guid.Empty) {
        continue;
      }
      var priority = Whizbang.Core.Priority.WorkPriority.Effective(row.Priority ?? Whizbang.Core.Priority.WorkPriority.UNDECLARED);
      if (folds.TryGetValue(streamId, out var fold)) {
        folds[streamId] = (
          Math.Min(fold.Priority, priority),
          fold.Oldest is null || (row.ReceivedAt is { } arrived && arrived < fold.Oldest) ? row.ReceivedAt ?? fold.Oldest : fold.Oldest,
          fold.Rows + 1);
      } else {
        order.Add(streamId);
        folds[streamId] = (priority, row.ReceivedAt, 1);
      }
    }
    var result = new List<InboxStreamFold>(order.Count);
    foreach (var streamId in order) {
      var (priority, oldest, count) = folds[streamId];
      result.Add(new InboxStreamFold(streamId, priority, oldest, count));
    }
    return result;
  }
}
