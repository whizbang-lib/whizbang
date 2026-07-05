using System.Diagnostics;
using Whizbang.Testing.Observability;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Builds <see cref="CapturedSpan"/> instances for tree/comparer tests without needing
/// real <see cref="Activity"/> instances.
/// </summary>
internal static class SpanFactory {
  public static CapturedSpan Create(
    string name,
    string traceId = "trace-1",
    string spanId = "span-1",
    string? parentSpanId = null,
    IReadOnlyDictionary<string, object?>? tags = null,
    ActivityKind kind = ActivityKind.Internal,
    ActivityStatusCode status = ActivityStatusCode.Unset,
    DateTimeOffset? startTime = null) {
    return new CapturedSpan {
      Name = name,
      Kind = kind,
      TraceId = traceId,
      SpanId = spanId,
      ParentSpanId = parentSpanId,
      Duration = TimeSpan.FromMilliseconds(1),
      Status = status,
      Tags = tags ?? new Dictionary<string, object?>(),
      Events = [],
      SourceName = "test-source",
      StartTime = startTime ?? DateTimeOffset.UnixEpoch
    };
  }
}
