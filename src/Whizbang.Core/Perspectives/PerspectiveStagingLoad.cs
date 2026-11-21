// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1822, S2325, RCS1163 // RED stub (#2248)
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

/// <summary>The staging load of a blue-green rebuild's shadow table (#2248).</summary>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveStagingLoadTests.cs</tests>
public sealed class PerspectiveStagingLoad {
  /// <summary>Begins a load of <paramref name="table"/>.</summary>
  /// <param name="table">The shadow table.</param>
  /// <param name="chunkRows">The rows a chunk holds.</param>
  public PerspectiveStagingLoad(string table, int chunkRows) {
    Table = table;
    ChunkRows = chunkRows;
  }

  /// <summary>The shadow table.</summary>
  public string Table { get; }

  /// <summary>The rows a chunk holds.</summary>
  public int ChunkRows { get; }

  /// <summary>The rows held.</summary>
  public int StagedCount => 0;

  /// <summary>The rows written.</summary>
  public long RowsLoaded => 0;

  /// <summary>Begins the load in this flow.</summary>
  /// <param name="load">The load.</param>
  /// <returns>A handle that ends it.</returns>
  public static IDisposable Begin(PerspectiveStagingLoad load) => throw new NotImplementedException();

  /// <summary>The load of <paramref name="table"/> in this flow.</summary>
  /// <param name="table">The table.</param>
  /// <returns>The load, or null.</returns>
  public static PerspectiveStagingLoad? For(string table) => null;

  /// <summary>Where the row stands.</summary>
  /// <param name="rowId">The row.</param>
  /// <returns>Its state.</returns>
  public PerspectiveStagedRowState StateOf(Guid rowId) => PerspectiveStagedRowState.Absent;

  /// <summary>Holds the write.</summary>
  /// <param name="write">The write.</param>
  /// <param name="sink">The sink.</param>
  /// <returns>Whether it is held.</returns>
  public bool TryStage(PendingPerspectiveWrite write, IPerspectiveBulkLoadSink sink) => false;

  /// <summary>Drops a held row.</summary>
  /// <param name="rowId">The row.</param>
  /// <returns>Whether it was held.</returns>
  public bool Unstage(Guid rowId) => false;

  /// <summary>Loads the held rows.</summary>
  /// <param name="cancellationToken">Cancels the load.</param>
  /// <returns>A task.</returns>
  public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>The rows refused since the last call.</summary>
  /// <returns>The refusals.</returns>
  public IReadOnlyList<PerspectiveStagedRowRefusal> TakeRefused() => [];
}
