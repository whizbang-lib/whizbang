namespace Whizbang.Core.Perspectives.System;

/// <summary>
/// Read model tracking the status of all registered perspectives.
/// Queryable via Lens for dashboards and operational tooling.
/// Developers subscribe to system rebuild/migration events which update this model.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#status-model</docs>
public sealed record PerspectiveStatusModel {
  /// <summary>
  /// Deterministic GUID derived from perspective name.
  /// </summary>
  [StreamId]
  public Guid Id { get; init; }

  /// <summary>
  /// Name of the perspective class.
  /// </summary>
  [PhysicalField(Indexed = true)]
  public string PerspectiveName { get; init; } = "";

  /// <summary>
  /// Current state of the perspective.
  /// </summary>
  [PhysicalField]
  public PerspectiveState State { get; init; }

  /// <summary>
  /// Current schema hash from migration tracking.
  /// </summary>
  public string? SchemaHash { get; init; }

  /// <summary>
  /// When the last rebuild was started.
  /// </summary>
  public DateTimeOffset? LastRebuildStartedAt { get; init; }

  /// <summary>
  /// When the last rebuild completed.
  /// </summary>
  public DateTimeOffset? LastRebuildCompletedAt { get; init; }

  /// <summary>
  /// Duration of the last rebuild.
  /// </summary>
  public TimeSpan? LastRebuildDuration { get; init; }

  /// <summary>
  /// Mode of the last rebuild.
  /// </summary>
  public RebuildMode? LastRebuildMode { get; init; }

  /// <summary>
  /// Last error message if the perspective is in a failed state.
  /// </summary>
  public string? LastError { get; init; }

  /// <summary>
  /// When this status record was last updated.
  /// </summary>
  public DateTimeOffset LastUpdatedAt { get; init; }
}

/// <summary>
/// Current state of a perspective.
/// </summary>
public enum PerspectiveState {
  /// <summary>Normal operation — processing events as they arrive.</summary>
  Active,
  /// <summary>Rebuild in progress — replaying events.</summary>
  Rebuilding,
  /// <summary>Blue-green migration in progress — new table being populated.</summary>
  MigratingBlueGreen,
  /// <summary>Last rebuild or migration failed.</summary>
  Failed,
  /// <summary>Perspective schema has changed but migration hasn't started.</summary>
  Stale
}
