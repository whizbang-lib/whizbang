using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Static helpers for lifecycle-hook Apply methods. Mirrors
/// <see cref="SagaApplyHelper"/> for items.
/// </summary>
/// <remarks>
/// <para>
/// Hook bookend events can arrive out of order (Started → Completed
/// isn't guaranteed by consumer transports). The helpers handle the
/// happy path AND the Completed-arrives-without-Started synthesis path
/// without surfacing inconsistent state.
/// </para>
/// <para>
/// All methods take an explicit <c>timestamp</c> instead of reaching for
/// <see cref="DateTimeOffset.UtcNow"/> so Apply purity is preserved
/// across rewinds.
/// </para>
/// </remarks>
public static class SagaHookApplyHelper {

  /// <summary>
  /// Pre-declares hooks on the saga projection at initiation time —
  /// typically called from <c>Apply(ISagaInitiatedEvent)</c> when
  /// <c>HookNames</c> is non-empty. Idempotent: existing rows (Pending,
  /// Running, or terminal) are preserved untouched.
  /// </summary>
  public static void DeclareHooks(BaseSagaModel saga, IEnumerable<string> hookNames, DateTimeOffset timestamp) {
    ArgumentNullException.ThrowIfNull(saga);
    ArgumentNullException.ThrowIfNull(hookNames);

    foreach (var name in hookNames) {
      if (saga.Hooks.Any(h => h.HookName == name)) {
        continue;
      }
      saga.Hooks.Add(new SagaHookExecution {
        HookName = name,
        Status = SagaItemState.Pending,
        CreatedAt = timestamp,
      });
    }
  }

  /// <summary>
  /// Find-or-create the hook execution row, transition to
  /// <see cref="SagaItemState.Running"/>, set
  /// <see cref="SagaHookExecution.StartedAt"/>. Replay-safe: existing
  /// terminal rows stay terminal; existing StartedAt is preserved.
  /// </summary>
  public static void TrackHookStarted(BaseSagaModel saga, string hookName, string? displayName, DateTimeOffset timestamp) {
    ArgumentNullException.ThrowIfNull(saga);
    ArgumentException.ThrowIfNullOrWhiteSpace(hookName);

    var hook = saga.Hooks.FirstOrDefault(h => h.HookName == hookName);
    if (hook is null) {
      saga.Hooks.Add(new SagaHookExecution {
        HookName = hookName,
        DisplayName = displayName,
        Status = SagaItemState.Running,
        CreatedAt = timestamp,
        StartedAt = timestamp,
      });
    } else if (!hook.IsTerminal) {
      hook.Status = SagaItemState.Running;
      hook.StartedAt ??= timestamp;
      if (hook.DisplayName is null && displayName is not null) {
        hook.DisplayName = displayName;
      }
    }
    saga.UpdatedAt = timestamp;
  }

  /// <summary>
  /// Find-or-create the hook execution row, transition to a terminal
  /// state, set <see cref="SagaHookExecution.CompletedAt"/> and error
  /// context. Synthesizes a row if the Started event was missed or
  /// arrived after the Completed event. Replay-safe: existing terminal
  /// rows are not overwritten.
  /// </summary>
  public static void TrackHookCompleted(
      BaseSagaModel saga,
      string hookName,
      SagaItemState finalStatus,
      string? errorMessage,
      string? errorDetails,
      DateTimeOffset timestamp) {
    ArgumentNullException.ThrowIfNull(saga);
    ArgumentException.ThrowIfNullOrWhiteSpace(hookName);

    var hook = saga.Hooks.FirstOrDefault(h => h.HookName == hookName);
    if (hook is null) {
      hook = new SagaHookExecution {
        HookName = hookName,
        CreatedAt = timestamp,
        StartedAt = timestamp,
      };
      saga.Hooks.Add(hook);
    }
    if (hook.IsTerminal) {
      return;
    }

    hook.Status = finalStatus;
    hook.CompletedAt = timestamp;
    hook.ErrorMessage = errorMessage;
    hook.ErrorDetails = errorDetails;
    saga.UpdatedAt = timestamp;
  }
}
