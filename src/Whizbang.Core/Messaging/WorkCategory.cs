namespace Whizbang.Core.Messaging;

/// <summary>
/// Identifies which work category a flush operation targets. Used by category-aware
/// methods like <see cref="IWorkCoordinator.RenewLeasesAsync"/> and
/// <see cref="IWorkCoordinator.ReportFailuresAsync"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public enum WorkCategory {
  /// <summary>Outbox messages awaiting transport publish.</summary>
  Outbox,

  /// <summary>Inbox messages awaiting handler dispatch.</summary>
  Inbox,

  /// <summary>Perspective event work items awaiting projection processing.</summary>
  PerspectiveEvent
}

/// <summary>
/// Helpers for converting <see cref="WorkCategory"/> values to the SQL function's
/// expected text values (snake_case).
/// </summary>
public static class WorkCategoryExtensions {
  /// <summary>Returns the snake_case wire form expected by the postgres functions.</summary>
  public static string ToSqlCategory(this WorkCategory category) => category switch {
    WorkCategory.Outbox => "outbox",
    WorkCategory.Inbox => "inbox",
    WorkCategory.PerspectiveEvent => "perspective_event",
    _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown WorkCategory")
  };
}
