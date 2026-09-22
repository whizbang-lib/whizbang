using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// Coverage round 23 tail: <see cref="CompositeFactory.Create{TConstituent}"/>'s
/// non-positive-byte-budget guard. <c>CompositeFactoryTests.Create_NonPositiveCountCap_ThrowsAsync</c>
/// covers the sibling <c>MaxConstituentsPerComposite</c> guard but nothing exercises a non-positive
/// <c>MaxBytesPerComposite</c>.
/// </summary>
[Category("Minting")]
public class CompositeFactoryCoverageTests {
  private sealed record ProbeConstituent(string Key, string Name, long Size);

  private sealed class ProbeComposite : CompositeEventBase {
    public IReadOnlyList<string> Names { get; init; } = [];
  }

  /// <summary>
  /// Operator impact: a zero or negative byte budget can never bound anything (every chunk would
  /// either flush immediately or never), so it is a producer configuration bug. Silently accepting
  /// it instead of failing loudly at mint time would leave the misconfiguration to surface later
  /// as an oversized or single-constituent-per-message plan with no clear cause.
  /// </summary>
  [Test]
  public async Task Create_NonPositiveByteBudget_ThrowsAsync() {
    var factory = new CompositeFactory();
    var request = new CompositeMintRequest<ProbeConstituent> {
      Constituents = [new ProbeConstituent("k", "a", 1)],
      GroupKey = CompositeGroupKey.FromKey<ProbeConstituent>(c => c.Key),
      BuildComposite = batch => new ProbeComposite { Names = [.. batch.Constituents.Select(c => c.Name)] },
      MaxBytesPerComposite = 0,
      ConstituentSizeBytes = c => c.Size,
    };

    var thrown = await Assert.That(() => factory.Create(request))
      .Throws<ArgumentOutOfRangeException>();

    await Assert.That(thrown!.Message).Contains("MaxBytesPerComposite must be positive")
      .Because("the message must name which bound was violated, not just that some argument was "
             + "out of range — this and the count-cap guard throw the same exception type");
  }
}
