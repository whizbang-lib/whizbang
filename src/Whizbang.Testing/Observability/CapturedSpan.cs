using System.Diagnostics;

namespace Whizbang.Testing.Observability;

/// <summary>
/// Immutable representation of a captured OpenTelemetry span for test assertions.
/// </summary>
/// <remarks>
/// <para>
/// This record captures all relevant span data at the time of activity completion,
/// allowing for structured assertions on trace output in integration tests.
/// </para>
/// <para>
/// Volatile fields like TraceId, SpanId, and Duration are captured but should be
/// excluded when comparing against baselines since they change between test runs.
/// </para>
/// </remarks>
public sealed record CapturedSpan {
  /// <summary>
  /// The span/activity name (e.g., "Lifecycle PostDistributeDetached", "Handler OrderReceptor").
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// The activity kind (Internal, Server, Client, Producer, Consumer).
  /// </summary>
  public required ActivityKind Kind { get; init; }

  /// <summary>
  /// The W3C trace ID (32 hex characters). Volatile - changes each run.
  /// </summary>
  public required string TraceId { get; init; }

  /// <summary>
  /// The span ID (16 hex characters). Volatile - changes each run.
  /// </summary>
  public required string SpanId { get; init; }

  /// <summary>
  /// The parent span ID (16 hex characters), or null if this is a root span.
  /// </summary>
  public required string? ParentSpanId { get; init; }

  /// <summary>
  /// The span duration. Volatile - varies between runs.
  /// </summary>
  public required TimeSpan Duration { get; init; }

  /// <summary>
  /// The span status (Unset, Ok, Error).
  /// </summary>
  public required ActivityStatusCode Status { get; init; }

  /// <summary>
  /// The span tags/attributes as key-value pairs.
  /// </summary>
  public required IReadOnlyDictionary<string, object?> Tags { get; init; }

  /// <summary>
  /// Events recorded on this span.
  /// </summary>
  public required IReadOnlyList<CapturedSpanEvent> Events { get; init; }

  /// <summary>
  /// The source name that emitted this activity (e.g., "Whizbang.Tracing").
  /// </summary>
  public required string SourceName { get; init; }

  /// <summary>
  /// Timestamp when the span started. Volatile - changes each run.
  /// </summary>
  public required DateTimeOffset StartTime { get; init; }

  /// <summary>
  /// Creates a CapturedSpan from a completed Activity.
  /// </summary>
  /// <param name="activity">The activity to capture. Must not be null.</param>
  /// <returns>An immutable CapturedSpan record.</returns>
  /// <exception cref="ArgumentNullException">Thrown if activity is null.</exception>
  public static CapturedSpan From(Activity activity) {
    ArgumentNullException.ThrowIfNull(activity);

    return new CapturedSpan {
      Name = activity.DisplayName,
      Kind = activity.Kind,
      TraceId = activity.TraceId.ToHexString(),
      SpanId = activity.SpanId.ToHexString(),
      ParentSpanId = activity.ParentSpanId == default
        ? null
        : activity.ParentSpanId.ToHexString(),
      Duration = activity.Duration,
      Status = activity.Status,
      Tags = activity.TagObjects.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
      Events = [.. activity.Events
        .Select(e => new CapturedSpanEvent {
          Name = e.Name,
          Timestamp = e.Timestamp,
          Tags = e.Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        })],
      SourceName = activity.Source.Name,
      StartTime = activity.StartTimeUtc
    };
  }

  /// <summary>
  /// Returns true if this span has no parent (is a root span).
  /// </summary>
  public bool IsRoot => ParentSpanId is null;

  /// <summary>
  /// Gets a tag value by key, or null if not found.
  /// </summary>
  /// <param name="key">The tag key.</param>
  /// <returns>The tag value or null.</returns>
  public object? GetTag(string key) =>
    Tags.TryGetValue(key, out var value) ? value : null;

  /// <summary>
  /// Gets a tag value as a specific type, or default if not found or wrong type.
  /// </summary>
  /// <typeparam name="T">The expected tag value type.</typeparam>
  /// <param name="key">The tag key.</param>
  /// <returns>The tag value cast to T, or default(T).</returns>
  public T? GetTag<T>(string key) =>
    Tags.TryGetValue(key, out var value) && value is T typed ? typed : default;
}

/// <summary>
/// Immutable representation of a span event for test assertions.
/// </summary>
public sealed record CapturedSpanEvent {
  /// <summary>
  /// The event name.
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// When the event occurred.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }

  /// <summary>
  /// Event attributes as key-value pairs.
  /// </summary>
  public required IReadOnlyDictionary<string, object?> Tags { get; init; }
}
