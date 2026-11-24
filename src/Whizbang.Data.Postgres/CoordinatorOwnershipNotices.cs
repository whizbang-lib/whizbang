// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The ownership notices the coordination SQL raises, and how a coordinator surfaces them so none is silent: a write
/// fenced by the partition epoch (<c>whizbang.fenced</c>, #2556) and a claim that found no valid partition map
/// (<c>whizbang.partition_map_unavailable=true</c>, #2554). Both PostgreSQL drivers' coordinators read them through here.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs</tests>
public static partial class CoordinatorOwnershipNotices {
  /// <summary>Raised by a write that fenced something; its detail is <c>kind=count</c>.</summary>
  public const string FENCED = "whizbang.fenced";

  /// <summary>Raised by <c>claim_work</c> in map mode when no valid partition map is published, so it claimed nothing.</summary>
  public const string PARTITION_MAP_UNAVAILABLE = "whizbang.partition_map_unavailable=true";

  /// <summary>
  /// Watches <paramref name="connection"/> for fenced writes for as long as the returned scope lives, and on dispose counts
  /// each kind on <see cref="WorkCoordinatorMetrics.FencedWrites"/> and logs what was fenced at debug.
  /// </summary>
  /// <param name="connection">The connection the write runs on.</param>
  /// <param name="metrics">The coordinator's metrics, when registered.</param>
  /// <param name="logger">The coordinator's logger, when it has one.</param>
  /// <param name="operation">The coordinator call making the write, named in the log.</param>
  /// <returns>The scope; dispose it once the write has run.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AFencedWrite_IsCountedByKind_AndLoggedAsync</tests>
  public static FencedWriteScope WatchFencedWrites(
      NpgsqlConnection connection, WorkCoordinatorMetrics? metrics, ILogger? logger, string operation) {
    ArgumentNullException.ThrowIfNull(connection);
    return new FencedWriteScope(connection, metrics, logger, operation);
  }

  /// <summary>
  /// Reports a claim's answer about the partition map: counts a claim that found none and warns once per streak, naming
  /// why the instance claims nothing, so a wait for a map is never silent.
  /// </summary>
  /// <param name="unavailable">Whether the claim raised <see cref="PARTITION_MAP_UNAVAILABLE"/>.</param>
  /// <param name="metrics">The coordinator's metrics, when registered; without them every such claim is warned.</param>
  /// <param name="logger">The coordinator's logger, when it has one.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AClaimWithNoValidMap_IsCounted_AndWarnedOncePerStreakAsync</tests>
  public static void ReportPartitionMap(bool unavailable, WorkCoordinatorMetrics? metrics, ILogger? logger) {
    var warn = metrics is null ? unavailable : metrics.RecordPartitionMapAvailability(unavailable);
    if (warn && logger is not null) {
      LogPartitionMapUnavailable(logger);
    }
  }

  /// <summary>Whether a notice is the fenced-write notice; its kind and count, when it is.</summary>
  /// <param name="messageText">The notice's message.</param>
  /// <param name="detail">The notice's detail, <c>kind=count</c>.</param>
  /// <param name="kind">The kind fenced.</param>
  /// <param name="count">How many were fenced.</param>
  /// <returns>True for a well-formed fenced notice.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AFencedNotice_IsReadAsItsKindAndCount_AndAnyOtherIsNotAsync</tests>
  public static bool TryReadFenced(string? messageText, string? detail, out string kind, out long count) {
    kind = "";
    count = 0;
    if (!string.Equals(messageText, FENCED, StringComparison.Ordinal) || detail is null) {
      return false;
    }
    var split = detail.LastIndexOf('=');
    if (split <= 0 || !long.TryParse(detail.AsSpan(split + 1), System.Globalization.NumberStyles.None,
          System.Globalization.CultureInfo.InvariantCulture, out count)) {
      return false;
    }
    kind = detail[..split];
    return true;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Fenced {Count} {Kind} write(s) in {Operation}: the partition's ownership moved on since the work was claimed, so the result stays for the current owner")]
  private static partial void LogFenced(ILogger logger, long count, string kind, string operation);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "This instance claims nothing: the service owns partitions by map and no valid partition map is published (none yet, or one naming an instance that is no longer live past its lease). Claims resume when the partition assigner publishes one. Logged once until a map is found again.")]
  private static partial void LogPartitionMapUnavailable(ILogger logger);

  /// <summary>Collects the fenced notices a write raises; counts and logs them on dispose.</summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AFencedWrite_IsCountedByKind_AndLoggedAsync</tests>
  public sealed class FencedWriteScope : IDisposable {
    private readonly NpgsqlConnection _connection;
    private readonly WorkCoordinatorMetrics? _metrics;
    private readonly ILogger? _logger;
    private readonly string _operation;
    private readonly List<(string Kind, long Count)> _fenced = [];

    internal FencedWriteScope(NpgsqlConnection connection, WorkCoordinatorMetrics? metrics, ILogger? logger, string operation) {
      _connection = connection;
      _metrics = metrics;
      _logger = logger;
      _operation = operation;
      _connection.Notice += _onNotice;
    }

    /// <summary>How many writes were fenced so far, all kinds together.</summary>
    public long Total {
      get {
        lock (_fenced) {
          return _fenced.Sum(f => f.Count);
        }
      }
    }

    /// <inheritdoc />
    public void Dispose() {
      _connection.Notice -= _onNotice;
      lock (_fenced) {
        foreach (var (kind, count) in _fenced) {
          _metrics?.FencedWrites.Add(count, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.KIND_TAG, kind));
          if (_logger is not null) {
            LogFenced(_logger, count, kind, _operation);
          }
        }
        _fenced.Clear();
      }
    }

    private void _onNotice(object? sender, NpgsqlNoticeEventArgs e) {
      if (TryReadFenced(e.Notice.MessageText, e.Notice.Detail, out var kind, out var count)) {
        lock (_fenced) {
          _fenced.Add((kind, count));
        }
      }
    }
  }
}
