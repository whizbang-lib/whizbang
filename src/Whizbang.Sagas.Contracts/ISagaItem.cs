namespace Whizbang.Sagas;

/// <summary>
/// Per-item record embedded in <c>BaseSagaModel.GetItems()</c>. Carries
/// the item's identifier, display name, terminal-state status, and
/// optional error context.
/// </summary>
/// <remarks>
/// Implementations are typically the concrete <c>SagaItemModel</c>
/// projection (per-item streams) or a saga-specific item record kept
/// inside the parent saga projection. Both shapes work with
/// <c>SagaApplyHelper</c>.
/// </remarks>
public interface ISagaItem {

  /// <summary>Caller-supplied stable identifier — typically a stringified id of the underlying domain entity.</summary>
  string ItemIdentifier { get; }

  /// <summary>Optional human-readable name (for UI, logs, dead-letter messages).</summary>
  string? DisplayName { get; }

  /// <summary>Current state.</summary>
  SagaItemState Status { get; }

  /// <summary>Most recent error message if the item failed.</summary>
  string? ErrorMessage { get; }

  /// <summary>True once the item is in a terminal state (Completed, Failed, or Skipped).</summary>
  bool IsTerminal { get; }
}
