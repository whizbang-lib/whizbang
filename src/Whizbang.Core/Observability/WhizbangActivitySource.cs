// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Core.Observability;

/// <summary>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:Execution_ActivitySource_IsInitializedAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:Transport_ActivitySource_IsInitializedAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveException_WithNullActivity_DoesNothingAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveException_WithActivity_SetsStatusAndTagsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveCancellation_WithNullActivity_DoesNothingAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveCancellation_WithActivity_SetsStatusAndTagsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:ExecutionActivitySource_CanCreateActivitiesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:TransportActivitySource_CanCreateActivitiesAsync</tests>
/// Central ActivitySource for Whizbang observability.
/// Provides distributed tracing and diagnostic instrumentation.
/// AOT-friendly, zero-allocation, cross-platform.
/// </summary>
/// <remarks>
/// <para>
/// These are <see cref="ActivitySource"/>s, not EventSources, so naming one as a dotnet-trace provider
/// (<c>--providers Whizbang.Execution</c>) collects nothing (#1864). Subscribe with the OpenTelemetry SDK; the wildcard
/// takes every Whizbang source, including <c>Whizbang.Hosting</c>, <c>Whizbang.Tracing</c> and
/// <c>Whizbang.MessageTags</c>, and any added later:
/// </para>
/// <code>
/// services.AddOpenTelemetry()
///   .WithTracing(builder => builder.AddSource("Whizbang.*"));
/// </code>
/// <para>
/// Without an SDK in the process, dotnet-trace reaches activities through the runtime's DiagnosticSource bridge: enable
/// <c>Microsoft-Diagnostics-DiagnosticSource</c> with its Events keyword (0x2; 0x3 adds its diagnostic messages) and an
/// activity filter naming the source. The filter matches one source by its exact name, or every source with <c>*</c>:
/// </para>
/// <code>
/// dotnet-trace collect -p &lt;pid&gt; --providers 'Microsoft-Diagnostics-DiagnosticSource:0x3:5:FilterAndPayloadSpecs="[AS]Whizbang.Execution"'
/// </code>
/// </remarks>
/// <docs>operations/observability/tracing#collecting-activities</docs>
public static class WhizbangActivitySource {
  private const string ACTIVITY_SOURCE_VERSION = "1.0.0";

  /// <summary>
  /// ActivitySource for execution strategies (SerialExecutor, ParallelExecutor).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:Execution_ActivitySource_IsInitializedAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:ExecutionActivitySource_CanCreateActivitiesAsync</tests>
  public static readonly ActivitySource Execution = new("Whizbang.Execution", ACTIVITY_SOURCE_VERSION);

  /// <summary>
  /// ActivitySource for transport operations (InProcessTransport, etc.).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:Transport_ActivitySource_IsInitializedAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:TransportActivitySource_CanCreateActivitiesAsync</tests>
  public static readonly ActivitySource Transport = new("Whizbang.Transport", ACTIVITY_SOURCE_VERSION);

  /// <summary>
  /// ActivitySource for hosting and initialization operations (Aspire AppHost, startup provisioning).
  /// Tracks infrastructure setup like topic/subscription creation, filter provisioning, and worker initialization.
  /// </summary>
  public static readonly ActivitySource Hosting = new("Whizbang.Hosting", ACTIVITY_SOURCE_VERSION);

  /// <summary>
  /// ActivitySource for handler tracing operations.
  /// Used by the ITracer implementation to emit handler invocation spans.
  /// Add this source to OpenTelemetry to see handler-level traces.
  /// </summary>
  /// <remarks>
  /// <code>
  /// services.AddOpenTelemetry()
  ///   .WithTracing(builder => builder.AddSource("Whizbang.Tracing"));
  /// </code>
  /// </remarks>
  public static readonly ActivitySource Tracing = new("Whizbang.Tracing", ACTIVITY_SOURCE_VERSION);

  /// <summary>
  /// ActivitySource for stream integrity (#2365): audits, answers, repair requests, repairs and re-checks. A request
  /// carries its span's trace link, so an origin's answer and a consumer's repair join the trace that found the gap.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-traces</docs>
  /// <tests>tests/Whizbang.Observability.Tests/IntegrityTracingTests.cs:TheSource_IsWhizbangIntegrity_AndIsListedWithEveryWhizbangSourceAsync</tests>
  public static readonly ActivitySource Integrity = new("Whizbang.Integrity.Stub", ACTIVITY_SOURCE_VERSION);

  /// <summary>
  /// Every activity source the framework publishes, the turnkey answer to "which sources do I subscribe?"
  /// (<c>AddWhizbangInstrumentation</c> on a tracer provider subscribes them all).
  /// </summary>
  /// <docs>operations/observability/tracing#collecting-activities</docs>
  /// <tests>tests/Whizbang.Observability.Tests/IntegrityTracingTests.cs:EveryActivitySourceWhizbangDeclares_IsOnTheListAsync</tests>
  public static IReadOnlyList<string> All { get; } = [];

  /// <summary>
  /// Records a defensive exception that should never occur in normal operation.
  /// Sets activity status to Error and adds exception details.
  /// </summary>
  /// <param name="activity">The current activity (may be null if no listener).</param>
  /// <param name="exception">The unexpected exception.</param>
  /// <param name="description">Description of where the exception occurred.</param>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveException_WithNullActivity_DoesNothingAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveException_WithActivity_SetsStatusAndTagsAsync</tests>
  public static void RecordDefensiveException(Activity? activity, Exception exception, string description) {
    if (activity == null) {
      return;
    }

    activity.SetStatus(ActivityStatusCode.Error, description);
    activity.AddTag("exception.type", TypeNameFormatter.DisplayName(exception.GetType()));
    activity.AddTag("exception.message", exception.Message);
    activity.AddTag("exception.stacktrace", exception.StackTrace);
    activity.AddTag("defensive.code", true);
  }

  /// <summary>
  /// Records a defensive cancellation that should never occur in normal operation.
  /// Sets activity status to Error and adds cancellation context.
  /// </summary>
  /// <param name="activity">The current activity (may be null if no listener).</param>
  /// <param name="context">Description of where the cancellation occurred.</param>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveCancellation_WithNullActivity_DoesNothingAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/WhizbangActivitySourceTests.cs:RecordDefensiveCancellation_WithActivity_SetsStatusAndTagsAsync</tests>
  public static void RecordDefensiveCancellation(Activity? activity, string context) {
    if (activity == null) {
      return;
    }

    activity.SetStatus(ActivityStatusCode.Error, $"Unexpected cancellation: {context}");
    activity.AddTag("cancellation.unexpected", true);
    activity.AddTag("defensive.code", true);
  }
}
