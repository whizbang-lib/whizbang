namespace Whizbang.Core.Lineage;

/// <summary>
/// The response every apply-stack serving surface returns. Degradation is honest and stated: a
/// host whose driver registers no <see cref="IApplyStackQuery"/>, or whose query throws (a store
/// mid-migration, a missing table), gets <see cref="Available"/> = false with the
/// <see cref="Reason"/> — never an empty list pretending to be an answer.
/// </summary>
/// <param name="Available">Whether the query surface answered.</param>
/// <param name="Reason">Why it did not, when <paramref name="Available"/> is false.</param>
/// <param name="Signatures">The path signatures, heaviest first, when available.</param>
/// <param name="Flow">The anchored flow view, when an anchor was requested and the query answered.</param>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackReporterTests.cs</tests>
public sealed record ApplyStackReport(
  bool Available,
  string? Reason,
  IReadOnlyList<ApplyPathSignature>? Signatures,
  ApplyStackFlowGraph? Flow);

/// <summary>
/// Builds the <see cref="ApplyStackReport"/> from whatever the host registered — the one shared
/// projection behind the minimal-API, FastEndpoints, and HotChocolate surfaces, so the transports
/// cannot drift apart in what they disclose (the same pattern the startup status surface uses).
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackReporterTests.cs</tests>
public static class ApplyStackReporter {
  /// <summary>
  /// Runs the signature query and, when <paramref name="anchorEventType"/> is given, computes the
  /// anchored flow view over the result. A null <paramref name="query"/> or a throwing query
  /// yields an unavailable report with the reason, not an exception — the surface must not share
  /// a failure domain with the store it reports on.
  /// </summary>
  /// <param name="query">The driver-supplied query, or null when the driver provides none.</param>
  /// <param name="options">Filters for the signature query.</param>
  /// <param name="anchorEventType">Anchor for the flow view; null or empty skips the flow.</param>
  /// <param name="radius">Flow radius either side of the anchor.</param>
  /// <param name="maxBranchesPerColumn">Long-tail collapse threshold per flow column.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  public static async Task<ApplyStackReport> BuildAsync(
      IApplyStackQuery? query,
      ApplyStackQueryOptions options,
      string? anchorEventType = null,
      int radius = 3,
      int maxBranchesPerColumn = 10,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(options);
    if (query is null) {
      return new ApplyStackReport(
        Available: false,
        Reason: "No IApplyStackQuery is registered — the configured data driver does not supply the apply-stack query surface.",
        Signatures: null,
        Flow: null);
    }

    try {
      var signatures = await query.GetPathSignaturesAsync(options, cancellationToken).ConfigureAwait(false);
      var flow = string.IsNullOrEmpty(anchorEventType)
        ? null
        : ApplyStackFlowView.Compute(signatures, anchorEventType, radius, maxBranchesPerColumn);
      return new ApplyStackReport(Available: true, Reason: null, Signatures: signatures, Flow: flow);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      return new ApplyStackReport(Available: false, Reason: ex.Message, Signatures: null, Flow: null);
    }
  }
}
