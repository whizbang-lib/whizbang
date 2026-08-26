using System.Globalization;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Reports concurrency settings that cannot take effect because stream parallelism is disabled.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParallelizeStreams</c> defaults to <c>false</c> on both <see cref="WorkCoordinatorOptions"/>
/// and <see cref="OrderedStreamProcessorOptions"/>, while <c>MaxConcurrentStreams</c> defaults to 16
/// and <c>MaxConcurrentDispatch</c> to 8. The shipped default therefore advertises concurrency the
/// runtime will not use — this is not a mistake an operator has to make, it is what they inherit.
/// </para>
/// <para>
/// Measured on a real pipeline: raising the drain width from 16 to 128 with parallelism disabled
/// moved throughput from 26 to 352 rows/min; the same width with parallelism enabled reached
/// 2,664 rows/min. The width appeared to help — enough to look like the answer — while the real
/// constraint went unnamed.
/// </para>
/// <para>
/// The two flags share the name <c>ParallelizeStreams</c> across different option types, so an
/// operator who greps for it, finds one, and measures a genuine improvement will reasonably stop.
/// Every finding therefore names the option TYPE, not just the property.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/InertConcurrencyReportTests.cs</tests>
public static class InertConcurrencyReport {

  /// <summary>
  /// Returns one human-readable finding per concurrency setting that cannot take effect.
  /// </summary>
  /// <remarks>
  /// Silent when the configuration is coherent — both when parallelism is enabled, and when the
  /// configured width is 1 (the operator asked for serial and is getting serial). A diagnostic that
  /// fires on a correct configuration trains everyone to ignore it, which is indistinguishable from
  /// not having written it.
  /// </remarks>
  /// <param name="coordinator">Work-coordinator options, or null when not configured.</param>
  /// <param name="orderedStream">Ordered-stream-processor options, or null when not configured.</param>
  /// <param name="outboxDrain">Outbox drain options, or null when not configured.</param>
  /// <param name="inboxDispatch">Inbox dispatch options, or null when not configured.</param>
  /// <returns>Findings, empty when nothing is inert.</returns>
  public static IReadOnlyList<string> Analyze(
      WorkCoordinatorOptions? coordinator,
      OrderedStreamProcessorOptions? orderedStream,
      OutboxDrainWorkerOptions? outboxDrain,
      InboxDispatchWorkerOptions? inboxDispatch) {

    var findings = new List<string>();

    // Width > 1 is what makes a disabled flag a CONTRADICTION rather than a choice. Width 1 with
    // parallelism off is a coherent request for serial processing and must stay quiet.
    var drainWidth = outboxDrain?.MaxConcurrentStreams ?? 1;
    var dispatchWidth = inboxDispatch?.MaxConcurrentDispatch ?? 1;

    if (coordinator is not null && !coordinator.ParallelizeStreams && drainWidth > 1) {
      findings.Add(string.Format(
        CultureInfo.InvariantCulture,
        "{0}.{1} is false, so {2}.{3} = {4} cannot take effect — outbox streams drain serially. "
      + "Set {0}.{1} = true to use the configured width.",
        nameof(WorkCoordinatorOptions), nameof(WorkCoordinatorOptions.ParallelizeStreams),
        nameof(OutboxDrainWorkerOptions), nameof(OutboxDrainWorkerOptions.MaxConcurrentStreams),
        drainWidth));
    }

    if (orderedStream is not null && !orderedStream.ParallelizeStreams && dispatchWidth > 1) {
      findings.Add(string.Format(
        CultureInfo.InvariantCulture,
        "{0}.{1} is false, so {2}.{3} = {4} cannot take effect — inbox dispatch runs serially. "
      + "Note this is a SEPARATE flag from {5}.{1}; enabling one does not enable the other.",
        nameof(OrderedStreamProcessorOptions), nameof(OrderedStreamProcessorOptions.ParallelizeStreams),
        nameof(InboxDispatchWorkerOptions), nameof(InboxDispatchWorkerOptions.MaxConcurrentDispatch),
        dispatchWidth,
        nameof(WorkCoordinatorOptions)));
    }

    return findings;
  }
}
