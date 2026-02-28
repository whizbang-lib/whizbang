// Template snippets for tracing and metrics code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using System.Threading.Tasks;
using Whizbang.Generators.Templates.Placeholders;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for tracing and metrics code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
/// <remarks>
/// <para>
/// These snippets support both explicit tracing ([TraceHandler] attribute) and
/// explicit metrics ([MetricHandler] attribute) on receptors.
/// </para>
/// <para>
/// Timing uses IDebuggerAwareClock to get accurate timestamps even when debugging.
/// Start and end times are captured and duration is calculated from the difference.
/// </para>
/// </remarks>
public class TracingSnippets {
  // Placeholder fields to make snippets compile
  protected IServiceProvider ServiceProvider => null!;
  protected IServiceScopeFactory _scopeFactory => null!;

  /// <summary>
  /// Snippet for traced receptor invocation with response.
  /// Wraps the handler call with tracing and metrics.
  /// Uses IDebuggerAwareClock to capture start/end times for accurate duration.
  /// </summary>
  protected async ValueTask<TResult> TracedInvokerWithResponseExample<TResult>(object msg) {
    #region TRACED_INVOKER_WITH_RESPONSE_SNIPPET
    // Capture timing with debug-aware clock (two timestamps for start/end)
    var clock = scope.ServiceProvider.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = scope.ServiceProvider.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    object? result = null;
    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      var typedMsg = (__MESSAGE_TYPE__)msg;
      result = await receptor.HandleAsync(typedMsg);
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = clock != null
          ? (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000
          : (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING__
    }
    #endregion

    return default!;
  }

  /// <summary>
  /// Snippet for traced void receptor invocation.
  /// Wraps the handler call with tracing and metrics.
  /// </summary>
  protected async ValueTask TracedVoidInvokerExample(object msg) {
    #region TRACED_VOID_INVOKER_SNIPPET
    // Capture timing with debug-aware clock
    var clock = scope.ServiceProvider.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = scope.ServiceProvider.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      var typedMsg = (__MESSAGE_TYPE__)msg;
      await receptor.HandleAsync(typedMsg);
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING__
    }
    #endregion
  }

  /// <summary>
  /// Snippet for metric recording code.
  /// Gets injected into traced snippets when [MetricHandler] is present.
  /// </summary>
  protected void MetricRecordingExample() {
    #region METRIC_RECORDING_SNIPPET
    // Record handler metrics via IHandlerMetrics
    var metrics = scope.ServiceProvider.GetService<global::Whizbang.Core.Tracing.IHandlerMetrics>();
    metrics?.RecordInvocation(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        status,
        durationMs,
        startTime,
        endTime);
    #endregion
  }

  /// <summary>
  /// Placeholder for no metric recording (when [MetricHandler] is not present).
  /// </summary>
  protected void NoMetricRecordingExample() {
    #region NO_METRIC_RECORDING_SNIPPET
    // No metrics recorded - [MetricHandler] not present
    #endregion
  }

  /// <summary>
  /// Snippet for sync receptor tracing with response.
  /// </summary>
  protected TResult TracedSyncInvokerWithResponseExample<TResult>(object msg) {
    #region TRACED_SYNC_INVOKER_WITH_RESPONSE_SNIPPET
    // Capture timing with debug-aware clock
    var clock = scope.ServiceProvider.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = scope.ServiceProvider.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    object? result = null;
    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      var typedMsg = (__MESSAGE_TYPE__)msg;
      result = receptor.Handle(typedMsg);
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING__
    }
    #endregion

    return default!;
  }

  /// <summary>
  /// Snippet for void sync receptor tracing.
  /// </summary>
  protected void TracedVoidSyncInvokerExample(object msg) {
    #region TRACED_VOID_SYNC_INVOKER_SNIPPET
    // Capture timing with debug-aware clock
    var clock = scope.ServiceProvider.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = scope.ServiceProvider.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      var typedMsg = (__MESSAGE_TYPE__)msg;
      receptor.Handle(typedMsg);
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING__
    }
    #endregion
  }

  /// <summary>
  /// Snippet for lifecycle receptor tracing (used in ReceptorRegistry).
  /// </summary>
  protected async ValueTask<object?> TracedLifecycleInvokerExample(IServiceProvider sp, object msg, CancellationToken ct) {
    #region TRACED_LIFECYCLE_INVOKER_SNIPPET
    // Capture timing with debug-aware clock
    var clock = sp.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = sp.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    object? result = null;
    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      result = await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
      // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
      if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {
        result = routedResult.Value;
      }
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING_LIFECYCLE__
    }
    #endregion

    return result;
  }

  /// <summary>
  /// Snippet for void lifecycle receptor tracing (used in ReceptorRegistry).
  /// </summary>
  protected async ValueTask<object?> TracedVoidLifecycleInvokerExample(IServiceProvider sp, object msg, CancellationToken ct) {
    #region TRACED_VOID_LIFECYCLE_INVOKER_SNIPPET
    // Capture timing with debug-aware clock
    var clock = sp.GetService<global::Whizbang.Core.Perspectives.Sync.IDebuggerAwareClock>();
    var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    // Get tracer for explicit trace output
    var tracer = sp.GetService<global::Whizbang.Core.Tracing.ITracer>();

    // Begin trace span if tracing is enabled for this handler
    tracer?.BeginHandlerTrace(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        __TRACE_VERBOSITY__,
        __HAS_TRACE_ATTRIBUTE__);

    System.Exception? handlerException = null;
    var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

    try {
      var receptor = sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
    } catch (System.Exception ex) {
      handlerException = ex;
      status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
      throw;
    } finally {
      // Capture end time
      var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
      var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

      // End trace span
      tracer?.EndHandlerTrace(
          "__RECEPTOR_CLASS__",
          typeof(__MESSAGE_TYPE__).Name,
          status,
          durationMs,
          startTime,
          endTime,
          handlerException);

      // Record metrics if [MetricHandler] is present
      __METRIC_RECORDING_LIFECYCLE__
    }
    return null;
    #endregion
  }

  /// <summary>
  /// Metric recording snippet for lifecycle invokers (uses sp parameter instead of scope).
  /// </summary>
  protected void MetricRecordingLifecycleExample() {
    #region METRIC_RECORDING_LIFECYCLE_SNIPPET
    // Record handler metrics via IHandlerMetrics
    var metrics = sp.GetService<global::Whizbang.Core.Tracing.IHandlerMetrics>();
    metrics?.RecordInvocation(
        "__RECEPTOR_CLASS__",
        typeof(__MESSAGE_TYPE__).Name,
        status,
        durationMs,
        startTime,
        endTime);
    #endregion
  }

  /// <summary>
  /// Placeholder for no metric recording in lifecycle invokers.
  /// </summary>
  protected void NoMetricRecordingLifecycleExample() {
    #region NO_METRIC_RECORDING_LIFECYCLE_SNIPPET
    // No metrics recorded - [MetricHandler] not present
    #endregion
  }
}
