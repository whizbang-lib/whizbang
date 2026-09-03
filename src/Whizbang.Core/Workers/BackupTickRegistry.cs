namespace Whizbang.Core.Workers;

/// <summary>
/// Default in-memory <see cref="IBackupTickRegistry"/> implementation.
/// Thread-safe; registration order is preserved.
/// </summary>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BackupTickRegistryTests.cs</tests>
public sealed class BackupTickRegistry : IBackupTickRegistry {
  private readonly Lock _lock = new();
  private readonly List<BackupTickRegistration> _registrations = [];

  /// <inheritdoc />
  public void Register(string name, Func<CancellationToken, Task> tick, Func<bool> isEnabled) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(tick);
    ArgumentNullException.ThrowIfNull(isEnabled);
    lock (_lock) {
      _registrations.Add(new BackupTickRegistration(name, tick, isEnabled));
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<BackupTickRegistration> Registrations {
    get {
      lock (_lock) {
        // Snapshot copy — callers iterate while other threads may register
        // concurrently. The snapshot is cheap (a few small records); no
        // reason to expose the internal list.
        return [.. _registrations];
      }
    }
  }
}
