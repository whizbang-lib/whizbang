// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The default <see cref="IPerspectiveStateSource"/>: per perspective of a batch, one read of the rows of every stream
/// not known to be new, and for the rows found missing one purge-marker query and one history query.
/// </summary>
/// <remarks>
/// <para>
/// Each read is made once, by the first pair of the perspective that asks, through that pair's own store, purge
/// markers or event store; every other pair of the perspective waits for the same read. The pairs of a batch are
/// prepared by runners of the same generated type, so whichever asks first reads exactly what any other would.
/// </para>
/// <para>
/// A read that fails is logged and answered as unknown, so each pair reads its own row per stream and meets the
/// failure (and its explanation, such as a stored document no reader takes) exactly as the per-pair path does.
/// </para>
/// </remarks>
/// <param name="logger">Logs a batched read that failed.</param>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/BatchedPerspectiveStateSourceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.StateSource.cs</tests>
public sealed partial class BatchedPerspectiveStateSource(ILogger<BatchedPerspectiveStateSource>? logger = null) : IPerspectiveStateSource {
  private readonly ILogger _logger = logger ?? NullLogger<BatchedPerspectiveStateSource>.Instance;

  /// <inheritdoc/>
  public IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs) {
    ArgumentNullException.ThrowIfNull(pairs);
    return new Batch(pairs, _logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "A batched {Read} for perspective {PerspectiveName} failed; each of its {StreamCount} stream(s) is read on its own")]
  private static partial void LogBatchedReadFailed(ILogger logger, Exception exception, string read, string perspectiveName, int streamCount);

  /// <summary>One batch's plan and the reads it has made, per perspective.</summary>
  private sealed class Batch : IPerspectiveBatchState {
    private readonly ILogger _logger;
    private readonly Dictionary<string, Perspective> _perspectives = new(StringComparer.Ordinal);

    public Batch(IReadOnlyList<PerspectiveStateRequest> pairs, ILogger logger) {
      _logger = logger;
      foreach (var pair in pairs) {
        if (!_perspectives.TryGetValue(pair.PerspectiveName, out var perspective)) {
          perspective = new Perspective(pair.PerspectiveName);
          _perspectives.Add(pair.PerspectiveName, perspective);
        }
        perspective.Pairs[pair.StreamId] = pair;
      }
    }

    public bool IsKnownNew(Guid streamId, string perspectiveName) =>
      _pair(streamId, perspectiveName) is { KnownNew: true };

    public async Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(
        IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
        where TModel : class {
      ArgumentNullException.ThrowIfNull(store);
      if (_pair(streamId, perspectiveName) is not { KnownNew: false } || !_perspectives.TryGetValue(perspectiveName, out var perspective)) {
        return null;
      }
      var rows = await perspective.ReadRowsOnceAsync(
        () => _readRowsAsync(store, perspective, cancellationToken)).ConfigureAwait(false);
      if (rows is not IReadOnlyDictionary<Guid, PerspectiveStoredRow<TModel>> typed) {
        return null;
      }
      return typed.TryGetValue(streamId, out var row)
        ? row
        : new PerspectiveStoredRow<TModel>(new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null), null);
    }

    public async Task<bool?> IsPurgedAsync(
        IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      ArgumentNullException.ThrowIfNull(markers);
      if (_missingStreams(streamId, perspectiveName) is not { } perspective) {
        return null;
      }
      var purged = await perspective.ReadPurgedOnceAsync(async () => {
        var missing = perspective.MissingStreams();
        try {
          return await markers.PurgedAmongAsync(missing, perspectiveName, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogBatchedReadFailed(_logger, ex, "purge-marker read", perspectiveName, missing.Count);
          return null;
        }
      }).ConfigureAwait(false);
      return purged?.Contains(streamId);
    }

    public async Task<bool?> HasHistoryBeforeAsync(
        IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) {
      ArgumentNullException.ThrowIfNull(eventStore);
      ArgumentNullException.ThrowIfNull(eventTypes);
      // The batch asks each stream before the first event it planned for it; any other question is the runner's own.
      if (_missingStreams(streamId, perspectiveName) is not { } perspective
          || perspective.Pairs[streamId].FirstEventId != beforeEventId) {
        return null;
      }
      var withHistory = await perspective.ReadHistoryOnceAsync(async () => {
        var probes = perspective.MissingStreams().Select(id => (id, perspective.Pairs[id].FirstEventId)).ToList();
        try {
          return await eventStore.StreamsWithEventsBeforeAsync(probes, eventTypes, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogBatchedReadFailed(_logger, ex, "history probe", perspectiveName, probes.Count);
          return null;
        }
      }).ConfigureAwait(false);
      return withHistory?.Contains(streamId);
    }

    private PerspectiveStateRequest? _pair(Guid streamId, string perspectiveName) =>
      _perspectives.TryGetValue(perspectiveName, out var perspective) && perspective.Pairs.TryGetValue(streamId, out var pair)
        ? pair
        : null;

    /// <summary>
    /// The perspective, when the batch read its rows and found this stream's missing: the probes are asked only of the
    /// rows the batched read reported absent.
    /// </summary>
    private Perspective? _missingStreams(Guid streamId, string perspectiveName) =>
      _perspectives.TryGetValue(perspectiveName, out var perspective) && perspective.IsMissing(streamId) ? perspective : null;

    private async Task<object?> _readRowsAsync<TModel>(
        IPerspectiveStore<TModel> store, Perspective perspective, CancellationToken cancellationToken) where TModel : class {
      var ids = perspective.Pairs.Values.Where(p => !p.KnownNew).Select(p => p.StreamId).ToList();
      try {
        var rows = await store.ReadForApplyManyAsync(ids, cancellationToken).ConfigureAwait(false);
        if (rows is not null) {
          perspective.SetMissing(ids.Where(id => !rows.ContainsKey(id)));
        }
        return rows;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogBatchedReadFailed(_logger, ex, "row read", perspective.Name, ids.Count);
        return null;
      }
    }
  }

  /// <summary>One perspective's pairs in a batch, and the reads made for them, each made once.</summary>
  private sealed class Perspective(string name) {
    private readonly object _gate = new();
    private Task<object?>? _rows;
    private Task<IReadOnlySet<Guid>?>? _purged;
    private Task<IReadOnlySet<Guid>?>? _history;
    private HashSet<Guid>? _missing;

    public string Name { get; } = name;

    public Dictionary<Guid, PerspectiveStateRequest> Pairs { get; } = [];

    public Task<object?> ReadRowsOnceAsync(Func<Task<object?>> read) {
      lock (_gate) {
        return _rows ??= read();
      }
    }

    public Task<IReadOnlySet<Guid>?> ReadPurgedOnceAsync(Func<Task<IReadOnlySet<Guid>?>> read) {
      lock (_gate) {
        return _purged ??= read();
      }
    }

    public Task<IReadOnlySet<Guid>?> ReadHistoryOnceAsync(Func<Task<IReadOnlySet<Guid>?>> read) {
      lock (_gate) {
        return _history ??= read();
      }
    }

    public void SetMissing(IEnumerable<Guid> missing) {
      lock (_gate) {
        _missing = [.. missing];
      }
    }

    public bool IsMissing(Guid streamId) {
      lock (_gate) {
        return _missing?.Contains(streamId) == true;
      }
    }

    public IReadOnlyCollection<Guid> MissingStreams() {
      lock (_gate) {
        return [.. _missing!];
      }
    }
  }
}
