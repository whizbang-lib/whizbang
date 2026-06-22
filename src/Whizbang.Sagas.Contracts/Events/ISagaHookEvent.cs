namespace Whizbang.Sagas;

/// <summary>
/// Base interface for lifecycle hook bookend events. Hooks are
/// consumer-named units of work the framework brackets with Started /
/// Completed events so projection state can show "pre-work" or
/// "post-work" steps as first-class items alongside saga items.
/// </summary>
public interface ISagaHookEvent : ISagaEvent {

  /// <summary>Identifier of the hook (consumer-defined name).</summary>
  string HookName { get; }

  /// <summary>Optional human-readable display name for UI.</summary>
  string? DisplayName { get; }
}
