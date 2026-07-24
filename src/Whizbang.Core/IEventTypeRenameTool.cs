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
/// Detects CLR type-name drift for pinned types, and (legacy) rewrites stored rows to the new name.
/// </summary>
/// <remarks>
/// <para>
/// Drift is detected by comparing <c>wh_message_type_registry.clr_type_name</c> for each
/// <c>pinned_id</c> against <see cref="IMessageTypeCatalog"/>. A pinned type whose code has
/// been renamed shows up as a <see cref="PendingRename"/>. <see cref="DetectRenamesAsync"/> stays a useful,
/// non-destructive diagnostic.
/// </para>
/// <para>
/// <b>The destructive apply path is superseded by the pinned-type ledger (rename platform).</b> Record the former
/// name in <c>.whizbang/pinned-type-ledger.json</c>; the ledger-aware registry reconcile
/// (<c>reconcile_message_type_registry</c>, migration 064) then heals <c>wh_message_type_registry</c> old → new
/// automatically and non-destructively on every startup, while the generated former-name JSON aliases keep events
/// stored under the old name deserializing. Rewriting stored type names across data tables — what
/// <see cref="ExecuteAsync"/> does — is no longer necessary.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
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
  [System.Obsolete("Superseded by the pinned-type ledger: record the former name in .whizbang/pinned-type-ledger.json and the ledger-aware registry reconcile heals it non-destructively. Will be removed in a future release.")]
  void Rename(string oldTypeName, string newTypeName);

  /// <summary>
  /// Applies every detected + manually-queued rename across the six data tables and the
  /// registry in a single transaction. Idempotent.
  /// </summary>
  [System.Obsolete("Superseded by the pinned-type ledger: record the former name in .whizbang/pinned-type-ledger.json and the ledger-aware registry reconcile (migration 064) heals wh_message_type_registry non-destructively on startup, while former-name aliases keep old events deserializing. Will be removed in a future release.")]
  Task ExecuteAsync(CancellationToken cancellationToken = default);
}
