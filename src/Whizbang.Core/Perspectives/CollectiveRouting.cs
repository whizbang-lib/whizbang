namespace Whizbang.Core.Perspectives;

/// <summary>
/// Constants for routing collective events through the perspective-work pipeline.
/// </summary>
public static class CollectiveRouting {
  /// <summary>
  /// The fixed sink perspective name that a collective event is routed to. The event-store chain
  /// (migration 061) creates exactly one <c>wh_perspective_events</c> row with this
  /// <c>perspective_name</c> per collective event (driven by <c>EventFlags.Collective</c>), and the
  /// <c>PerspectiveWorker</c> special-cases this name to dispatch the event through
  /// <see cref="ICollectiveDispatcher"/> exactly once instead of running a per-stream perspective.
  /// <para>
  /// Mirrors the <c>'__collective__'</c> SQL literal in migration 061 — keep the two in sync.
  /// </para>
  /// </summary>
#pragma warning disable CA1707 // editorconfig requires all_upper constants; CA1707 forbids underscores — codebase convention is to suppress CA1707 (see EventCategoryMetrics.Tags).
  public const string SINK_PERSPECTIVE_NAME = "__collective__";
#pragma warning restore CA1707
}
