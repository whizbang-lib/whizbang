namespace Whizbang.Core;

/// <summary>
/// A single rename detected (or manually queued) between a stored CLR type name and the
/// current code's CLR type name for the same pinned id.
/// </summary>
/// <param name="PinnedId">The pinned id that both names point at. Null when the entry comes
///   from a manual <see cref="IEventTypeRenameTool.Rename"/> call — registry row matching
///   then falls back to <paramref name="OldClrTypeName"/>.</param>
/// <param name="OldClrTypeName">The CLR type name currently stored in the registry and data tables.</param>
/// <param name="NewClrTypeName">The CLR type name the current code uses.</param>
public sealed record PendingRename(
  string? PinnedId,
  string OldClrTypeName,
  string NewClrTypeName
);

/// <summary>
/// Detects and applies CLR type name drift for pinned types. Used whenever a namespace
/// rename happens — Whizbang does not reconcile drift automatically; running this tool
/// is the single deliberate step that rewrites stored rows to the new name.
/// </summary>
/// <remarks>
/// <para>
/// Drift is detected by comparing <c>wh_message_type_registry.clr_type_name</c> for each
/// <c>pinned_id</c> against <see cref="IMessageTypeCatalog"/>. A pinned type whose code has
/// been renamed shows up as a <see cref="PendingRename"/>.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> applies all pending renames — the registry row and every data
/// table that stores the CLR type name — inside a single transaction. Idempotent: running
/// it twice is safe. Unpinned types are out of scope — tag with <c>[PinnedId]</c> before
/// renaming or accept that old rows become unresolvable.
/// </para>
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
public interface IEventTypeRenameTool {
  /// <summary>
  /// Compares registry state against the current code and returns every detected drift.
  /// Non-destructive.
  /// </summary>
  Task<IReadOnlyList<PendingRename>> DetectRenamesAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Queues a manual rename. Does not apply until <see cref="ExecuteAsync"/> is called.
  /// </summary>
  /// <param name="oldTypeName">CLR type name currently stored.</param>
  /// <param name="newTypeName">CLR type name to rewrite to.</param>
  void Rename(string oldTypeName, string newTypeName);

  /// <summary>
  /// Applies every detected + manually-queued rename across the six data tables and the
  /// registry in a single transaction. Idempotent.
  /// </summary>
  Task ExecuteAsync(CancellationToken cancellationToken = default);
}
