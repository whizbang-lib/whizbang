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
  /// Find-or-create item, mark <see cref="SagaItemState.Failed"/>, bump
  /// <see cref="BaseSagaModel.FailedItems"/> under
  /// <see cref="SagaItemModel.IsTerminal"/> guard, then call
  /// <see cref="BaseSagaModel.TryFailFast"/> — aborts the saga
  /// immediately without waiting for remaining items.
  /// </summary>
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
