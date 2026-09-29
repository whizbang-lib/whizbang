using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Static helpers for saga projection Apply methods. Extract the
/// find-or-create + IsTerminal-dedup + counter-bump pattern that is
/// identical across every saga projection. Apply methods stay pure —
/// these helpers just eliminate mechanical boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// Helpers operate on a saga's embedded item list — the
/// <c>List&lt;TItem&gt;</c> pattern used by consumer projections that
/// surface per-item detail directly on the saga row (for one-roundtrip
/// dashboard rendering). Sagas using the per-item-projection pattern
/// (separate <c>SagaItemModel</c> rows) typically don't call these —
/// counter updates happen in the saga's Apply for the per-item events
/// directly.
/// </para>
/// <para>
/// <c>TItem</c> is constrained to <see cref="SagaItemModel"/> + <c>new()</c>
/// so the helpers can construct a fresh item on first observation.
/// Consumers with domain-specific item types derive from
/// <see cref="SagaItemModel"/> and pass <c>List&lt;MyItem&gt;</c>.
/// </para>
/// </remarks>
public static class SagaApplyHelper {

  /// <summary>
  /// Find-or-create item, mark <see cref="SagaItemState.Completed"/>,
  /// bump <see cref="BaseSagaModel.CompletedItems"/> under
  /// <see cref="SagaItemModel.IsTerminal"/> guard, then call
  /// <see cref="BaseSagaModel.TryComplete"/>.
  /// </summary>
  public static void TrackCompleted<TItem>(
      BaseSagaModel saga,
      List<TItem> items,
      Guid sagaId,
      string sagaName,
      string itemIdentifier,
      DateTimeOffset timestamp,
      string? displayName = null) where TItem : SagaItemModel, new() {

    ArgumentNullException.ThrowIfNull(saga);
    ArgumentNullException.ThrowIfNull(items);

    var item = items.FirstOrDefault(x => x.ItemIdentifier == itemIdentifier);
    if (item is null) {
      item = new TItem {
        SagaId = sagaId,
        SagaName = sagaName,
        ItemIdentifier = itemIdentifier,
        DisplayName = displayName,
        State = SagaItemState.Completed,
        StartedAt = timestamp,
        CompletedAt = timestamp,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
      };
      items.Add(item);
      saga.CompletedItems++;
    } else if (!item.IsTerminal) {
      item.State = SagaItemState.Completed;
      item.CompletedAt = timestamp;
      item.UpdatedAt = timestamp;
      saga.CompletedItems++;
    }
    saga.UpdatedAt = timestamp;
    saga.TryComplete(itemIdentifier, timestamp);
  }

  /// <summary>
  /// Find-or-create item, mark <see cref="SagaItemState.Failed"/>, bump
  /// <see cref="BaseSagaModel.FailedItems"/> under
  /// <see cref="SagaItemModel.IsTerminal"/> guard, then call
  /// <see cref="BaseSagaModel.TryComplete"/>. The saga continues —
  /// remaining items still get a chance to run. Use
  /// <see cref="TrackFailedFast"/> for sagas where partial completion is
  /// unrecoverable.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Records one item's failure on the saga: the saga and its item list, the identity of both, the message and the time. TrackFailedFast takes the same shape and the two are chosen between at the call site.")]
  public static void TrackFailed<TItem>(
      BaseSagaModel saga,
      List<TItem> items,
      Guid sagaId,
      string sagaName,
      string itemIdentifier,
      string errorMessage,
      DateTimeOffset timestamp,
      string? errorDetails = null,
      string? displayName = null) where TItem : SagaItemModel, new() {

    ArgumentNullException.ThrowIfNull(saga);
    ArgumentNullException.ThrowIfNull(items);

    var item = items.FirstOrDefault(x => x.ItemIdentifier == itemIdentifier);
    if (item is null) {
      item = new TItem {
        SagaId = sagaId,
        SagaName = sagaName,
        ItemIdentifier = itemIdentifier,
        DisplayName = displayName,
        State = SagaItemState.Failed,
        StartedAt = timestamp,
        FailedAt = timestamp,
        ErrorMessage = errorMessage,
        ErrorDetails = errorDetails,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
      };
      items.Add(item);
      saga.FailedItems++;
    } else if (!item.IsTerminal) {
      item.State = SagaItemState.Failed;
      item.FailedAt = timestamp;
      item.ErrorMessage = errorMessage;
      item.ErrorDetails = errorDetails;
      item.UpdatedAt = timestamp;
      saga.FailedItems++;
    }
    saga.UpdatedAt = timestamp;
    saga.TryComplete(itemIdentifier, timestamp);
  }

  /// <summary>
  /// Record that the watchdog abandoned the saga, from the perspective that applies
  /// <c>SagaCompletionAbandonedEvent</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// No item is involved, which is what distinguishes this from the three above: nothing failed and
  /// nothing completed. The saga stopped making progress with nothing left to resolve, and the
  /// watchdog stopped waiting for it.
  /// </para>
  /// <para>
  /// Wiring this is what stops the stranded-saga sweep re-arming the saga once per
  /// <c>StrandedSagaRearmInterval</c> and publishing its abandonment again each time. A saga whose
  /// perspective does not apply the event stays merely incomplete and keeps being re-armed, exactly
  /// as before -- the framework publishes the event but cannot record it on a model it does not own.
  /// </para>
  /// </remarks>
  /// <param name="saga">The saga model the perspective holds.</param>
  /// <param name="timestamp">When the watchdog gave up, from the event.</param>
  /// <returns>Whether this call was the one that recorded it.</returns>
  public static bool TrackAbandoned(BaseSagaModel saga, DateTimeOffset timestamp) {
    ArgumentNullException.ThrowIfNull(saga);
    return saga.TryAbandon(timestamp);
  }

  /// <summary>
  /// Find-or-create item, mark <see cref="SagaItemState.Failed"/>, bump
  /// <see cref="BaseSagaModel.FailedItems"/> under
  /// <see cref="SagaItemModel.IsTerminal"/> guard, then call
  /// <see cref="BaseSagaModel.TryFailFast"/> — aborts the saga
  /// immediately without waiting for remaining items.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Records one item's failure on the saga: the saga and its item list, the identity of both, the message and the time. Callers pass these straight through from the receptor's own arguments, so a parameter object would be built at every call site and read only here.")]
  public static void TrackFailedFast<TItem>(
      BaseSagaModel saga,
      List<TItem> items,
      Guid sagaId,
      string sagaName,
      string itemIdentifier,
      string errorMessage,
      DateTimeOffset timestamp,
      string? errorDetails = null,
      string? displayName = null) where TItem : SagaItemModel, new() {

    ArgumentNullException.ThrowIfNull(saga);
    ArgumentNullException.ThrowIfNull(items);

    var item = items.FirstOrDefault(x => x.ItemIdentifier == itemIdentifier);
    if (item is null) {
      item = new TItem {
        SagaId = sagaId,
        SagaName = sagaName,
        ItemIdentifier = itemIdentifier,
        DisplayName = displayName,
        State = SagaItemState.Failed,
        StartedAt = timestamp,
        FailedAt = timestamp,
        ErrorMessage = errorMessage,
        ErrorDetails = errorDetails,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
      };
      items.Add(item);
      saga.FailedItems++;
    } else if (!item.IsTerminal) {
      item.State = SagaItemState.Failed;
      item.FailedAt = timestamp;
      item.ErrorMessage = errorMessage;
      item.ErrorDetails = errorDetails;
      item.UpdatedAt = timestamp;
      saga.FailedItems++;
    }
    saga.UpdatedAt = timestamp;
    saga.TryFailFast(itemIdentifier, timestamp);
  }
}
