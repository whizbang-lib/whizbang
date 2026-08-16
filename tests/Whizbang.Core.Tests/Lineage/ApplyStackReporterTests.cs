#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;

namespace Whizbang.Core.Tests.Lineage;

/// <summary>
/// The shared projection behind every apply-stack serving surface. Locks honest degradation — a
/// missing driver query and a throwing query are stated conditions with reasons, never empty
/// lists — and that the flow view is computed only when an anchor is requested.
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
public class ApplyStackReporterTests {

  private sealed class FixedQuery(IReadOnlyList<ApplyPathSignature> signatures) : IApplyStackQuery {
    public ApplyStackQueryOptions? SeenOptions { get; private set; }

    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) {
      SeenOptions = options;
      return Task.FromResult(signatures);
    }

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<Guid>>([]);
  }

  private sealed class ThrowingQuery : IApplyStackQuery {
    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("relation wh_event_store does not exist");

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("relation wh_event_store does not exist");
  }

  private static ApplyPathSignature _sig(long streams, params string[] path) =>
    new(path, streams, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  [Test]
  public async Task BuildAsync_NoQueryRegistered_ReportsUnavailableWithReasonAsync() {
    var report = await ApplyStackReporter.BuildAsync(query: null, new ApplyStackQueryOptions());

    await Assert.That(report.Available).IsFalse()
      .Because("a driver that supplies no query is a stated condition, not an empty result");
    await Assert.That(report.Reason).Contains("IApplyStackQuery");
    await Assert.That(report.Signatures).IsNull();
  }

  [Test]
  public async Task BuildAsync_ThrowingQuery_ReportsUnavailableWithTheExceptionReasonAsync() {
    var report = await ApplyStackReporter.BuildAsync(new ThrowingQuery(), new ApplyStackQueryOptions());

    await Assert.That(report.Available).IsFalse()
      .Because("a store mid-migration is an honest answer — the surface must not share its failure domain");
    await Assert.That(report.Reason).Contains("wh_event_store");
  }

  [Test]
  public async Task BuildAsync_NoAnchor_ReturnsSignaturesWithoutAFlowAsync() {
    var query = new FixedQuery([_sig(3, "Created", "Closed")]);

    var report = await ApplyStackReporter.BuildAsync(query, new ApplyStackQueryOptions());

    await Assert.That(report.Available).IsTrue();
    await Assert.That(report.Signatures!).Count().IsEqualTo(1);
    await Assert.That(report.Flow).IsNull()
      .Because("the flow view is computed only when an anchor is requested");
  }

  [Test]
  public async Task BuildAsync_WithAnchor_ComputesTheFlowOverTheSignaturesAsync() {
    var query = new FixedQuery([_sig(3, "Created", "Closed")]);

    var report = await ApplyStackReporter.BuildAsync(
      query, new ApplyStackQueryOptions(), anchorEventType: "Created", radius: 1);

    await Assert.That(report.Flow).IsNotNull();
    await Assert.That(report.Flow!.AnchorEventType).IsEqualTo("Created");
    await Assert.That(report.Flow.Nodes).Contains(new ApplyStackFlowNode(0, "Created", 3));
  }

  [Test]
  public async Task BuildAsync_PassesTheOptionsThroughToTheQueryAsync() {
    var query = new FixedQuery([]);
    var options = new ApplyStackQueryOptions { PerspectiveName = "OrderList", MaxSignatures = 7 };

    _ = await ApplyStackReporter.BuildAsync(query, options);

    await Assert.That(query.SeenOptions).IsSameReferenceAs(options)
      .Because("the surface adds nothing to the query — filters pass through unchanged");
  }

  [Test]
  public async Task BuildAsync_Cancellation_PropagatesRatherThanReportingUnavailableAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var query = new CancellingQuery();

    await Assert.That(async () => await ApplyStackReporter.BuildAsync(
        query, new ApplyStackQueryOptions(), cancellationToken: cts.Token))
      .Throws<OperationCanceledException>()
      .Because("a cancelled caller is not a degraded store — cancellation is never converted into a reason string");
  }

  [Test]
  public async Task BuildStreamsAsync_NoQueryRegistered_ReportsUnavailableWithReasonAsync() {
    var report = await ApplyStackReporter.BuildStreamsAsync(
      query: null, ["Created", "Closed"], new ApplyStackQueryOptions());

    await Assert.That(report.Available).IsFalse();
    await Assert.That(report.Reason).Contains("IApplyStackQuery");
    await Assert.That(report.Streams).IsNull();
  }

  [Test]
  public async Task BuildStreamsAsync_ThrowingQuery_ReportsUnavailableWithTheExceptionReasonAsync() {
    var report = await ApplyStackReporter.BuildStreamsAsync(
      new ThrowingQuery(), ["Created", "Closed"], new ApplyStackQueryOptions());

    await Assert.That(report.Available).IsFalse();
    await Assert.That(report.Reason).Contains("wh_event_store");
  }

  [Test]
  public async Task BuildStreamsAsync_PassesThroughTheDrillInResultAsync() {
    var streamId = Guid.NewGuid();
    var query = new FixedStreamsQuery([streamId]);

    var report = await ApplyStackReporter.BuildStreamsAsync(
      query, ["Created", "Closed"], new ApplyStackQueryOptions(), limit: 5);

    await Assert.That(report.Available).IsTrue();
    await Assert.That(report.Streams!).Contains(streamId);
    await Assert.That(query.SeenLimit).IsEqualTo(5)
      .Because("the surface adds nothing to the drill-in — the limit passes through unchanged");
  }

  private sealed class FixedStreamsQuery(IReadOnlyList<Guid> streams) : IApplyStackQuery {
    public int SeenLimit { get; private set; }

    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<ApplyPathSignature>>([]);

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) {
      SeenLimit = limit;
      return Task.FromResult(streams);
    }
  }

  private sealed class CancellingQuery : IApplyStackQuery {
    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) =>
      Task.FromCanceled<IReadOnlyList<ApplyPathSignature>>(cancellationToken);

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) =>
      Task.FromCanceled<IReadOnlyList<Guid>>(cancellationToken);
  }
}
