using System.Threading;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides ambient access to the current <see cref="ProcessingMode"/> via AsyncLocal.
/// Used by <see cref="PerspectiveRebuilder"/> to propagate rebuild mode to generated runners
/// without changing the <see cref="IPerspectiveRunner"/> interface.
/// </summary>
/// <remarks>
/// The ambient mode is picked up by generated runners when creating <see cref="LifecycleExecutionContext"/>
/// instances for lifecycle receptor invocation. This ensures receptors without
/// <see cref="FireDuringReplayAttribute"/> are suppressed during rebuild operations.
/// </remarks>
public static class ProcessingModeAccessor {
  private static readonly AsyncLocal<ProcessingMode?> _current = new();

  /// <summary>
  /// Gets or sets the ambient processing mode for the current async context.
  /// Null indicates normal live processing.
  /// </summary>
  public static ProcessingMode? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
