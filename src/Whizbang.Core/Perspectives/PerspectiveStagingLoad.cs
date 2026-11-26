// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>Where a row stands in a staging load.</summary>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
public enum PerspectiveStagedRowState {
  /// <summary>The load has not written the row, and holds none for it.</summary>
  Absent = 0,

  /// <summary>The load holds the row and has not written it yet.</summary>
  Staged = 1,

  /// <summary>The load wrote the row into its table.</summary>
  Loaded = 2,
}

/// <summary>A sink that loads many new rows into an empty table at once, the fastest way its database has.</summary>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.cs</tests>
public interface IPerspectiveBulkLoadSink {
  /// <summary>Loads the rows.</summary>
  /// <param name="writes">The rows, each an insert or an upsert of a row the table does not hold.</param>
  /// <param name="cancellationToken">Cancels the load.</param>
  /// <returns>One outcome per write, at the write's index.</returns>
  Task<IReadOnlyList<PerspectiveWriteOutcome>> LoadAsync(
      IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default);
}

/// <summary>A row a staging load could not write, and why.</summary>
/// <param name="RowId">The row.</param>
/// <param name="Error">What the table or the sink reported.</param>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
public readonly record struct PerspectiveStagedRowRefusal(Guid RowId, Exception Error);

/// <summary>
/// The staging load of a blue-green rebuild's shadow table (#2248): the rows the rebuild's first pass writes are held
/// here and loaded into the table in chunks, through the fastest path the driver has (<see cref="IPerspectiveBulkLoadSink"/>),
/// instead of one upsert per row.
/// </summary>
/// <remarks>
/// <para>
/// The shadow table starts empty and only the rebuild writes it, so the load knows every row it holds is absent from the
/// table, and every row it has not written is absent too. A store in the load's flow answers a read of such a row
/// without asking the database, holds an insert or an upsert of it here (<see cref="TryStage"/>), and drops a held row it
/// purges (<see cref="Unstage"/>). A row the store reads while it is held is loaded first (<see cref="FlushAsync"/>), and a
/// row already loaded is written and read the per-row way, so the ordering guard sees every write that follows the first.
/// </para>
/// <para>
/// A row the sink does not write (the table refused it) is absent, and is reported once through
/// <see cref="TakeRefused"/>, which is how the rebuild fails that row's stream and only it. A load belongs to one flow:
/// the rebuild replays its streams one at a time, and the load is not safe for concurrent use.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveStagingLoadTests.cs</tests>
public sealed class PerspectiveStagingLoad {
  private static readonly AsyncLocal<PerspectiveStagingLoad?> _current = new();

  private readonly Dictionary<Guid, PendingPerspectiveWrite> _staged = [];
  private readonly HashSet<Guid> _loaded = [];
  private readonly List<PerspectiveStagedRowRefusal> _refused = [];
  private IPerspectiveBulkLoadSink? _sink;

  /// <summary>Begins a load of <paramref name="table"/>.</summary>
  /// <param name="table">The shadow table's name, without a schema.</param>
  /// <param name="chunkRows">How many held rows the rebuild loads at once.</param>
  public PerspectiveStagingLoad(string table, int chunkRows) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentOutOfRangeException.ThrowIfLessThan(chunkRows, 1);
    Table = table;
    ChunkRows = chunkRows;
  }

  /// <summary>The shadow table's name, without a schema.</summary>
  public string Table { get; }

  /// <summary>How many held rows the rebuild loads at once.</summary>
  public int ChunkRows { get; }

  /// <summary>How many rows the load holds and has not written yet.</summary>
  public int StagedCount => _staged.Count;

  /// <summary>How many rows the load has written into the table.</summary>
  public long RowsLoaded { get; private set; }

  /// <summary>Begins <paramref name="load"/> in this async flow until the returned handle is disposed.</summary>
  /// <param name="load">The load.</param>
  /// <returns>A handle that restores the flow's previous load.</returns>
  public static IDisposable Begin(PerspectiveStagingLoad load) {
    ArgumentNullException.ThrowIfNull(load);
    var outer = _current.Value;
    _current.Value = load;
    return new Handle(outer);
  }

  /// <summary>
  /// The load this flow writes <paramref name="table"/> through, or null when it has none: matched by the table's own
  /// name, ignoring a schema prefix and double quotes.
  /// </summary>
  /// <param name="table">A table name, bare (<c>t</c>) or schema-qualified (<c>s.t</c>, <c>"s"."t"</c>).</param>
  /// <returns>The load, or null.</returns>
  public static PerspectiveStagingLoad? For(string table) {
    ArgumentNullException.ThrowIfNull(table);
    if (_current.Value is not { } load) {
      return null;
    }
    var name = table[(table.LastIndexOf('.') + 1)..].Trim('"');
    return string.Equals(name, load.Table, StringComparison.Ordinal) ? load : null;
  }

  /// <summary>Where the row stands in the load.</summary>
  /// <param name="rowId">The row.</param>
  /// <returns>Its state.</returns>
  public PerspectiveStagedRowState StateOf(Guid rowId) {
    if (_staged.ContainsKey(rowId)) {
      return PerspectiveStagedRowState.Staged;
    }
    return _loaded.Contains(rowId) ? PerspectiveStagedRowState.Loaded : PerspectiveStagedRowState.Absent;
  }

  /// <summary>
  /// Holds <paramref name="write"/> for the next chunk when it inserts or upserts a row the load has not written and does
  /// not hold; otherwise holds nothing, and the caller writes the row the per-row way (after
  /// <see cref="FlushAsync"/>, for a held row).
  /// </summary>
  /// <param name="write">The write.</param>
  /// <param name="sink">The sink the chunk is loaded through; the first one a load is given loads all of it.</param>
  /// <returns>Whether the write is held.</returns>
  public bool TryStage(PendingPerspectiveWrite write, IPerspectiveBulkLoadSink sink) {
    ArgumentNullException.ThrowIfNull(write);
    ArgumentNullException.ThrowIfNull(sink);
    if (write.Operation is not (PerspectiveWriteOperation.Insert or PerspectiveWriteOperation.Upsert)
        || StateOf(write.StreamId) != PerspectiveStagedRowState.Absent) {
      return false;
    }
    _sink ??= sink;
    _staged[write.StreamId] = write;
    return true;
  }

  /// <summary>Drops a held row, which a purge removes before it was ever written.</summary>
  /// <param name="rowId">The row.</param>
  /// <returns>Whether the load held it.</returns>
  public bool Unstage(Guid rowId) => _staged.Remove(rowId);

  /// <summary>Writes every held row into the table through the sink, and records the ones it refused.</summary>
  /// <param name="cancellationToken">Cancels the load.</param>
  /// <returns>A task that completes once the chunk is written.</returns>
  public async Task FlushAsync(CancellationToken cancellationToken) {
    if (_staged.Count == 0) {
      return;
    }
    var writes = _staged.Values.ToList();
    var outcomes = await _sink!.LoadAsync(writes, cancellationToken).ConfigureAwait(false);
    _staged.Clear();
    for (var i = 0; i < writes.Count; i++) {
      var outcome = outcomes[i];
      if (outcome.Status == PerspectiveWriteStatus.Written) {
        _loaded.Add(writes[i].StreamId);
        RowsLoaded++;
      } else {
        _refused.Add(new PerspectiveStagedRowRefusal(writes[i].StreamId, outcome.Error
          ?? new InvalidOperationException($"The staging load did not write row {writes[i].StreamId}: {outcome.Status}.")));
      }
    }
  }

  /// <summary>The rows the load could not write since the last call, each reported once.</summary>
  /// <returns>The refusals.</returns>
  public IReadOnlyList<PerspectiveStagedRowRefusal> TakeRefused() {
    var refused = _refused.ToList();
    _refused.Clear();
    return refused;
  }

  private sealed class Handle(PerspectiveStagingLoad? outer) : IDisposable {
    public void Dispose() => _current.Value = outer;
  }
}
