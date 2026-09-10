using Whizbang.Core.Messaging;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Folds the inbox rows a claim returned into one entry per stream for the batch hooks (priority step 1): the
/// stream's most urgent row (<see cref="WorkPriority.MostUrgent(IEnumerable{int})"/>, the same fold a minted
/// composite takes from its members), its oldest arrival and how many of its rows the batch holds. Rows of other
/// sources, and rows a claim_work that predates the columns returns without them, fold to the standard band.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimedInboxStreamFolderTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs</tests>
internal static class ClaimedInboxStreamFolder {
  private const string SOURCE_INBOX = "inbox";

  /// <summary>Folds <paramref name="rows"/> by stream, in the order each stream first appears (the claim's bucket order).</summary>
  internal static IReadOnlyList<InboxStreamFold> Fold(IReadOnlyList<WorkBatchRow> rows) {
    var order = new List<Guid>();
    var streams = new Dictionary<Guid, List<WorkBatchRow>>();
    foreach (var row in rows) {
      if (!string.Equals(row.Source, SOURCE_INBOX, StringComparison.Ordinal) || row.StreamId is not { } streamId || streamId == Guid.Empty) {
        continue;
      }
      if (!streams.TryGetValue(streamId, out var members)) {
        members = [];
        streams[streamId] = members;
        order.Add(streamId);
      }
      members.Add(row);
    }
    var result = new List<InboxStreamFold>(order.Count);
    foreach (var streamId in order) {
      var members = streams[streamId];
      result.Add(new InboxStreamFold(
        streamId,
        // Every stored row has an effective number (the store never writes zero), so the fold is over effective numbers.
        WorkPriority.MostUrgent(members.Select(static m => WorkPriority.Effective(m.Priority ?? WorkPriority.UNDECLARED))),
        members.Min(static m => m.ReceivedAt),
        members.Count));
    }
    return result;
  }
}
