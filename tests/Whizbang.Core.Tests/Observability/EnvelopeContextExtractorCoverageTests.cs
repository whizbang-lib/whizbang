using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: the null/empty-hops guards on
/// <see cref="EnvelopeContextExtractor.ExtractTraceContext"/> and
/// <see cref="EnvelopeContextExtractor.ExtractScope"/> when called DIRECTLY. Existing tests only
/// exercise the null/empty case through <see cref="EnvelopeContextExtractor.ExtractFromHops"/>,
/// which short-circuits before ever calling either of these — so their own guards have never run.
/// Both methods are public and called directly elsewhere (they are the documented consolidation
/// point for ReceptorInvoker/LifecycleInvokerTemplate), so a caller bypassing ExtractFromHops is a
/// real, reachable path.
/// </summary>
[Category("Observability")]
public class EnvelopeContextExtractorCoverageTests {

  /// <summary>
  /// Operator impact: a caller that goes straight to ExtractTraceContext (skipping
  /// ExtractFromHops) with no hops must get a safe default ActivityContext, not an exception that
  /// would break trace correlation for the whole request.
  /// </summary>
  [Test]
  public async Task ExtractTraceContext_NullHops_ReturnsDefaultAsync() {
    var result = EnvelopeContextExtractor.ExtractTraceContext(null);

    await Assert.That(result).IsEqualTo(default(System.Diagnostics.ActivityContext));
  }

  /// <summary>Operator impact: same default-safety guarantee for an empty (non-null) hop list.</summary>
  [Test]
  public async Task ExtractTraceContext_EmptyHops_ReturnsDefaultAsync() {
    var result = EnvelopeContextExtractor.ExtractTraceContext([]);

    await Assert.That(result).IsEqualTo(default(System.Diagnostics.ActivityContext));
  }

  /// <summary>
  /// Operator impact: a caller that goes straight to ExtractScope with no hops must get null
  /// (no security scope), not an exception — a missing scope must fail closed, not crash the
  /// caller.
  /// </summary>
  [Test]
  public async Task ExtractScope_NullHops_ReturnsNullAsync() {
    var result = EnvelopeContextExtractor.ExtractScope(null);

    await Assert.That(result).IsNull();
  }

  /// <summary>Operator impact: same fail-closed guarantee for an empty (non-null) hop list.</summary>
  [Test]
  public async Task ExtractScope_EmptyHops_ReturnsNullAsync() {
    var result = EnvelopeContextExtractor.ExtractScope([]);

    await Assert.That(result).IsNull();
  }
}
