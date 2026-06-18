using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Cross-cutting metrics for non-regular event categories — collective
/// events (set-mutation descriptors) and composite events (wire-only
/// inner-event bundles). Both share the same observability shape:
/// </summary>
/// <list type="bullet">
///   <item><description>A dispatch / expansion happens with a measurable duration.</description></item>
///   <item><description>It produces a fan-out factor (affected rows for collective; inner-event count for composite).</description></item>
///   <item><description>It can fail; failures classify into a small enum of <c>error_class</c> values.</description></item>
/// </list>
/// <remarks>
/// <para>
/// Meter name: <c>Whizbang.EventCategories</c>. All instruments take a
/// <c>category</c> tag (<c>collective</c> | <c>composite</c>) so a
/// single dashboard pivots on event category without juggling two
/// meter names. Additional tags (<c>event_type</c>,
/// <c>event_namespace</c>, <c>scope_kind</c>, <c>model_type</c>,
/// <c>error_class</c>) are added at the call site only where the
/// value is already in scope — no extra reflection, no extra lookups,
/// no new fields.
/// </para>
/// <para>
/// <strong>Tag cardinality safety.</strong>
/// <c>event_type</c> is the FQN (bounded by the application's deployed
/// event types). <c>event_namespace</c> is the namespace portion
/// (much lower cardinality, useful for cross-team dashboards).
/// <c>scope_kind</c> and <c>category</c> are very low cardinality
/// (handful of values). <c>model_type</c> is bounded by the perspective
/// count. None of the tags carry stream / tenant / instance ids — those
/// would explode cardinality.
/// </para>
/// </remarks>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/EventCategoryMetricsTests.cs</tests>
public sealed class EventCategoryMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.EventCategories";
#pragma warning restore CA1707

  /// <summary>
  /// Dispatch / expansion duration (ms) at the seam — at
  /// <c>CollectiveDispatcher.DispatchAsync</c> for collective, at the
  /// transport consumer's composite-expansion site for composite. Tags:
  /// <c>category</c>, <c>event_type</c>, <c>event_namespace</c>.
  /// </summary>
  public Histogram<double> DispatchDuration { get; }

  /// <summary>
  /// Fan-out factor: affected-row count for collective, inner-event
  /// count for composite. One bucketed observation per successful
  /// dispatch. Tags: <c>category</c>, <c>event_type</c>,
  /// <c>event_namespace</c>, and (collective only) <c>model_type</c>.
  /// </summary>
  public Histogram<int> Fanout { get; }

  /// <summary>
  /// Counter of dispatches per category. Tags: <c>category</c>,
  /// <c>event_type</c>, <c>event_namespace</c>, and (collective only)
  /// <c>scope_kind</c>.
  /// </summary>
  public Counter<long> Dispatched { get; }

  /// <summary>
  /// Counter of dispatch failures. Tags: <c>category</c>,
  /// <c>event_type</c>, <c>event_namespace</c>, <c>error_class</c>
  /// (e.g. <c>resolver_missing</c>, <c>executor_missing</c>,
  /// <c>handler_missing</c>, <c>expansion_limit_exceeded</c>,
  /// <c>sql_exception</c>, <c>unknown</c>).
  /// </summary>
  public Counter<long> Errors { get; }

  /// <summary>
  /// Initializes the metrics group.
  /// </summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public EventCategoryMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    DispatchDuration = meter.CreateHistogram<double>(
      "whizbang.event_category.dispatch.duration",
      "ms",
      "Dispatch / expansion duration at the category seam (collective dispatcher or composite expander)");

    Fanout = meter.CreateHistogram<int>(
      "whizbang.event_category.fanout",
      description: "Fan-out factor — affected rows for collective, inner-event count for composite");

    Dispatched = meter.CreateCounter<long>(
      "whizbang.event_category.dispatched",
      description: "Dispatches per category");

    Errors = meter.CreateCounter<long>(
      "whizbang.event_category.errors",
      description: "Dispatch failures per category");
  }

  /// <summary>
  /// Well-known tag keys used by this metric group. Centralized as
  /// constants so call sites don't drift on spelling.
  /// </summary>
#pragma warning disable CA1707
  public static class Tags {
    /// <summary><c>collective</c> | <c>composite</c>.</summary>
    public const string CATEGORY = "category";
    /// <summary>The event payload's CLR <c>FullName</c>.</summary>
    public const string EVENT_TYPE = "event_type";
    /// <summary>The event payload's CLR <c>Namespace</c>.</summary>
    public const string EVENT_NAMESPACE = "event_namespace";
    /// <summary>Collective only — the resolver discriminator (e.g. <c>tenant</c>).</summary>
    public const string SCOPE_KIND = "scope_kind";
    /// <summary>Collective only — the perspective's <c>TModel</c> name.</summary>
    public const string MODEL_TYPE = "model_type";
    /// <summary>One of a small enum of failure-classification strings.</summary>
    public const string ERROR_CLASS = "error_class";
  }
#pragma warning restore CA1707

  /// <summary>
  /// Well-known values for the <see cref="Tags.CATEGORY"/> tag. Keep
  /// short and stable — changing them requires updating downstream
  /// dashboards.
  /// </summary>
#pragma warning disable CA1707
  public static class Categories {
    /// <summary>An <c>ICollectiveEvent</c> dispatched through <c>ICollectiveDispatcher</c>.</summary>
    public const string COLLECTIVE = "collective";
    /// <summary>An <c>ICompositeEvent</c> expanded at the transport consumer boundary.</summary>
    public const string COMPOSITE = "composite";
  }
#pragma warning restore CA1707

  /// <summary>
  /// Well-known values for the <see cref="Tags.ERROR_CLASS"/> tag.
  /// </summary>
#pragma warning disable CA1707
  public static class ErrorClasses {
    /// <summary>No <c>ICollectiveScopeResolver</c> registered for the event's <c>ScopeKind</c>.</summary>
    public const string RESOLVER_MISSING = "resolver_missing";
    /// <summary>No <c>ICollectiveEventExecutor</c> registered for the entry's <c>ModelType</c>.</summary>
    public const string EXECUTOR_MISSING = "executor_missing";
    /// <summary>Composite expander's <c>MaxInnerEventsAllowed</c> cap was exceeded.</summary>
    public const string EXPANSION_LIMIT_EXCEEDED = "expansion_limit_exceeded";
    /// <summary>Catch-all for SQL provider exceptions.</summary>
    public const string SQL_EXCEPTION = "sql_exception";
    /// <summary>Anything else.</summary>
    public const string UNKNOWN = "unknown";
  }
#pragma warning restore CA1707
}
