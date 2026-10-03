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
  /// <summary>The live table of the perspective, or null when the perspective has none registered.</summary>
  /// <param name="perspectiveName">The perspective's CLR type name, as the rebuilder names it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task<string?> FindTableAsync(string perspectiveName, CancellationToken cancellationToken);

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
}

/// <summary>One blue-green swap: which table becomes live, and what happens to the one it replaces.</summary>
/// <param name="LiveTable">The live table.</param>
/// <param name="ShadowTable">The rebuilt table that replaces it.</param>
/// <param name="KeepPrevious">Whether the replaced table is kept under another name rather than dropped.</param>
/// <param name="LockTimeout">How long the swap waits for its locks before it gives up.</param>
public sealed record PerspectiveTableSwap(string LiveTable, string ShadowTable, bool KeepPrevious, TimeSpan LockTimeout);

/// <summary>Options for <see cref="IPerspectiveRebuilder.RebuildBlueGreenAsync"/>.</summary>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.cs</tests>
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
}
