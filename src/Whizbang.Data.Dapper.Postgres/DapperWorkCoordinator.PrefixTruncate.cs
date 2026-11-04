// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The prefix truncate on the Dapper driver: the gated truncate, the events it removes, the archived detail it keeps,
/// the perspectives the full-history guard asks about, the event version a compaction truncates through, and the
/// apply-path fold that runs before the pointers go.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's defaults for all six, so on this driver the prefix truncator told the
/// destruction hook, the Pre stages and any compaction handler that data was about to be destroyed, then reported
/// <c>unsupported</c> having removed nothing (#2043). Each member now runs the statement the EF Core coordinator runs,
/// from <see cref="StreamPrefixTruncateSql"/>, qualified with this coordinator's schema. An empty fold returns before a
/// connection is opened.
/// </remarks>
/// <docs>fundamentals/events/event-streams#prefix-truncate-on-every-data-layer</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamPrefixTruncateTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorNamedSchemaTests.cs:PrefixTruncate_RunsInTheConfiguredSchemaAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PrefixTruncateDriverParityTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task<StreamTruncateResult> TruncateStreamPrefixAsync(
      Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.TruncateAsync(cmd, prefix, streamId, throughVersion, archive, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<EphemeralDestructionTarget>> GetStreamTruncateTargetsAsync(
      Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.TargetsAsync(cmd, prefix, streamId, throughVersion, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<ArchivedEvent>> GetArchivedEventsAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.ArchivedEventsAsync(cmd, prefix, streamId, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(
      Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.ConsumingPerspectiveNamesAsync(cmd, prefix, streamId, throughVersion, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<long?> GetEventVersionAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.EventVersionAsync(cmd, prefix, eventId, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<int> FoldStreamApplyPathsAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.FoldStreamApplyPathsAsync(cmd, prefix, streamIds, cancellationToken), cancellationToken);
  }
}
