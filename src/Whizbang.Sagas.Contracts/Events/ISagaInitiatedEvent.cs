namespace Whizbang.Sagas;

/// <summary>
/// First event in a saga's lifecycle. Carries the item identifiers the
/// saga will track and, optionally, the names of lifecycle hooks the
/// consumer wants the framework to bookend.
/// </summary>
public interface ISagaInitiatedEvent : ISagaEvent {

  /// <summary>Caller-supplied stable identifiers for every item this saga will track. Order is irrelevant.</summary>
  IReadOnlyList<string> ItemIdentifiers { get; }

  /// <summary>Total item count — should equal <c>ItemIdentifiers.Count</c> but is supplied separately for forward-compat with streaming/lazy dispatch.</summary>
  int TotalItems { get; }

  /// <summary>Names of lifecycle hooks the saga will run. Each name turns into a pair of bookend events when <c>TryRunHookAsync</c> fires.</summary>
  IReadOnlyList<string>? HookNames { get; }
}
