using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Guards the assumption the jsonb containment rewrite rests on: that the loaded Entity Framework
/// Core and Npgsql provider are versions the rewrite was actually verified against.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite reaches into the provider's expression tree and emits an operator through a type in
/// the provider's Internal namespace. That is the correct trade today, because there is no public
/// seam and the alternative is a full scan on every filtered perspective. It is also exactly the
/// kind of thing that breaks quietly on an upgrade, which is why this test exists: bumping either
/// package past the verified range fails here, with a message saying what to re-verify, instead of
/// silently changing the SQL that consumers run.
/// </para>
/// <para>
/// Re-verifying means running the container-backed containment tests and confirming both halves
/// still hold: the plan uses the GIN index, and the containment filter returns the same rows as the
/// equality filter.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
[Category("Shard1")]
public class ProviderCapabilitiesTests {
  /// <summary>Entity Framework Core is inside the range the rewrite was verified against.</summary>
  [Test]
  public async Task EfCoreVersion_IsInsideTheVerifiedRangeAsync() {
    await Assert.That(ProviderCapabilities.EfCoreVersion)
      .IsGreaterThanOrEqualTo(ProviderCapabilities.ValidatedEfCoreMinimum);

    await Assert.That(ProviderCapabilities.EfCoreVersion)
      .IsLessThan(ProviderCapabilities.ValidatedEfCoreExclusiveMaximum);
  }

  /// <summary>The Npgsql provider is inside the range the rewrite was verified against.</summary>
  [Test]
  public async Task NpgsqlProviderVersion_IsInsideTheVerifiedRangeAsync() {
    await Assert.That(ProviderCapabilities.NpgsqlProviderVersion)
      .IsGreaterThanOrEqualTo(ProviderCapabilities.ValidatedNpgsqlMinimum);

    await Assert.That(ProviderCapabilities.NpgsqlProviderVersion)
      .IsLessThan(ProviderCapabilities.ValidatedNpgsqlExclusiveMaximum);
  }

  /// <summary>
  /// The combined verdict, which is what the rewriter and any diagnostic should consult rather than
  /// re-deriving the comparison.
  /// </summary>
  [Test]
  public async Task Combination_IsReportedAsVerifiedAsync() {
    await Assert.That(ProviderCapabilities.IsValidatedCombination).IsTrue();
  }

  /// <summary>
  /// While no provider translates containment itself, the framework has to. When that changes, this
  /// test is the reminder to stand the rewrite down rather than keep a private path alive.
  /// </summary>
  [Test]
  public async Task RewriteIsStillRequired_BecauseNoProviderDoesItAsync() {
    await Assert.That(ProviderCapabilities.NativeContainmentTranslationAvailable).IsFalse();
    await Assert.That(ProviderCapabilities.ContainmentRewriteRequired).IsTrue();
  }

  /// <summary>The description carries both loaded versions, so a log line is enough to diagnose.</summary>
  [Test]
  public async Task Describe_NamesBothLoadedVersionsAsync() {
    var description = ProviderCapabilities.Describe();

    await Assert.That(description).Contains(ProviderCapabilities.EfCoreVersion.ToString());
    await Assert.That(description).Contains(ProviderCapabilities.NpgsqlProviderVersion.ToString());
    await Assert.That(description).Contains("verified");
  }
}
