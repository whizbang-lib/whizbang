// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The table operations a blue-green perspective rebuild needs from its driver: a shadow table shaped like
/// the live one, rows removed from it, and the swap that makes it the live table.
/// </summary>
/// <remarks>
/// <para>
/// The rebuild replays into the shadow table while readers keep reading the live one, which stays complete
/// and unchanged by the rebuild. Writers keep writing the live table too; the rebuild catches the shadow up
/// with the streams they changed, and the last of that happens inside <see cref="SwapAsync"/> while the live
/// table is closed to writers but still open to readers. The swap itself is one transaction, so a reader sees
/// the old table or the new one and never a table in between.
/// </para>
/// <para>
/// A driver that registers no swapper rebuilds blue-green in place, as every driver did before, and logs
/// that it did.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/PostgresPerspectiveTableSwapperTests.cs</tests>
public interface IPerspectiveTableSwapper {
  /// <summary>The live table of the perspective's model, or null when it has none registered.</summary>
  /// <remarks>
  /// Keyed by the <strong>model</strong>, not the perspective: the schema pass writes the registry under the
  /// model's type name, so a perspective name never matches a row. Accepts either rendering of that name, the
  /// CLR form with '+' before a nested type or the generated fully-qualified form, dotted and optionally
  /// 'global::'-prefixed.
  /// </remarks>
  /// <param name="modelTypeName">The model's type name, in either rendering.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken);

  /// <summary>
  /// Creates an empty table shaped like <paramref name="liveTable"/>, with its columns, defaults, constraints and
  /// indexes, replacing one a failed rebuild left behind. Returns its name.
  /// </summary>
  Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken);

  /// <summary>Deletes the rows with the given ids from <paramref name="table"/>.</summary>
  Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

  /// <summary>
  /// Closes the live table to writers, runs <paramref name="underWriteLock"/>, and makes the shadow table the live
  /// one in the same transaction, keeping or dropping the previous table as <paramref name="swap"/> says.
  /// </summary>
  /// <remarks>
  /// Readers are never blocked while <paramref name="underWriteLock"/> runs; the rename at the end waits for the
  /// readers in flight, for at most <see cref="PerspectiveTableSwap.LockTimeout"/>, and readers that arrive while it
  /// waits queue behind it. A writer blocked by the swap continues against the new table once it commits.
  /// </remarks>
  /// <returns>The name the previous table was kept under, or null when it was dropped.</returns>
  Task<string?> SwapAsync(PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken);

  /// <summary>Drops <paramref name="table"/> when it exists: a shadow table a failed rebuild leaves.</summary>
  Task DropAsync(string table, CancellationToken cancellationToken);

  /// <summary>
  /// The one perspective that writes <paramref name="table"/>, as the perspective registry records it, or null when the
  /// table is not a perspective table, or more than one perspective writes it, so no single rebuild can rebuild it.
  /// </summary>
  /// <remarks>Default: null, so no table is rewritten by a rebuild on a driver that cannot say.</remarks>
  /// <param name="table">The bare table name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>operations/infrastructure/storage-maintenance#blue-green-rewrite</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/PostgresPerspectiveTableSwapperTests.cs</tests>
  Task<string?> FindPerspectiveAsync(string table, CancellationToken cancellationToken) =>
    Task.FromResult<string?>(null);

  /// <summary>
  /// Drops the shadow table's secondary indexes before a staging load writes it, and returns the statements that build
  /// them again (#2248). An index a constraint or a uniqueness rule needs stays, so the load refuses the same rows the
  /// per-row write would.
  /// </summary>
  /// <remarks>Default: none deferred, so every index is kept up to date through the load.</remarks>
  /// <param name="shadowTable">The shadow table, as <see cref="CreateShadowAsync"/> named it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The statements that build the dropped indexes, in order.</returns>
  /// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.Staging.cs</tests>
  Task<IReadOnlyList<string>> DeferIndexesAsync(string shadowTable, CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<string>>([]);

  /// <summary>Builds the indexes <see cref="DeferIndexesAsync"/> dropped, once the staging load has written every row.</summary>
  /// <remarks>Default: nothing to build.</remarks>
  /// <param name="shadowTable">The shadow table.</param>
  /// <param name="definitions">The statements <see cref="DeferIndexesAsync"/> returned.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.Staging.cs</tests>
  Task BuildDeferredIndexesAsync(string shadowTable, IReadOnlyList<string> definitions, CancellationToken cancellationToken) =>
    Task.CompletedTask;
}

/// <summary>One blue-green swap: which table becomes live, and what happens to the one it replaces.</summary>
/// <param name="LiveTable">The live table.</param>
/// <param name="ShadowTable">The rebuilt table that replaces it.</param>
/// <param name="KeepPrevious">Whether the replaced table is kept under another name rather than dropped.</param>
/// <param name="LockTimeout">How long the swap waits for its locks before it gives up.</param>
public sealed record PerspectiveTableSwap(string LiveTable, string ShadowTable, bool KeepPrevious, TimeSpan LockTimeout);

/// <summary>Options for <see cref="IPerspectiveRebuilder.RebuildBlueGreenAsync"/>.</summary>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.cs</tests>
public sealed class BlueGreenRebuildOptions {
  /// <summary>
  /// Whether the table the rebuild replaces is kept, renamed, rather than dropped. Default true: the previous
  /// table is the way back if the rebuilt one is wrong. A later rebuild replaces a kept table.
  /// </summary>
  public bool KeepPreviousTable { get; set; } = true;

  /// <summary>
  /// How many times the rebuild catches the shadow table up with streams written during the rebuild before it
  /// swaps; the swap catches up the remainder with the live table closed to writers. Default 3.
  /// </summary>
  public int MaxCatchUpPasses { get; set; } = 3;

  /// <summary>How long the swap waits for its locks before the rebuild fails. Default 10 seconds.</summary>
  public TimeSpan SwapLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

  /// <summary>
  /// Whether the first pass writes the empty shadow table through a staging load: its rows held and loaded in chunks the
  /// fastest way the driver has, with the secondary indexes built after the load (#2248). Default true, and chosen for
  /// every blue-green rebuild; a service can only switch it off. Without it each row is upserted on its own.
  /// </summary>
  /// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
  public bool StagingLoad { get; set; } = true;

  /// <summary>How many rows a staging load holds before it writes them. Default 1,000.</summary>
  /// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
  public int StagingChunkRows { get; set; } = 1_000;
}
