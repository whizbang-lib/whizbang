#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Targeted coverage for <see cref="StreamGroupClosure.Compute"/>'s no-membership guard — a
/// branch the broader <see cref="StreamGroupClosureTests"/> suite never reaches because every
/// scenario there seeds the cascade from a model that already owns at least one membership.
/// </summary>
/// <remarks>
/// <c>memberships</c> comes from the caller (typically <c>PerspectiveStreamGroupRegistry</c>), and
/// every SIBLING the closure walks to is guaranteed to be a key in it (siblings are only ever
/// discovered by iterating the registry's own entries) — but a SEED is external input, supplied
/// by whatever destroyed the row this cycle. A model that never joined any stream group (like the
/// <c>C</c> perspective the existing scenario deliberately excludes) is a perfectly ordinary
/// destruction the closure must shrug off, not a malformed input.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/StreamGroupClosure.cs</code-under-test>
public class StreamGroupClosureCoverageTests {

  private sealed class A;
  private sealed class B;
  private sealed class NonMember;

  [Test]
  public async Task Compute_SeedModelWithNoMembershipEntryAtAll_YieldsNoCascadeAsync() {
    // If this threw instead of skipping, a housekeeping sweep that evicts a row belonging to a
    // perspective outside the stream-group system entirely (the common case — most perspectives
    // never join a group) would crash the whole cycle instead of cascading zero rows.
    var row = Guid.NewGuid();
    var memberships = new Dictionary<Type, IReadOnlyList<StreamGroupMembership>> {
      [typeof(A)] = [new StreamGroupMembership("g1", Announce: true, Follow: true, Bridge: false)],
      [typeof(B)] = [new StreamGroupMembership("g1", Announce: true, Follow: true, Bridge: false)],
      // NonMember deliberately absent from the dictionary — it never joined a group.
    };

    var cascade = StreamGroupClosure.Compute([(typeof(NonMember), row)], memberships);

    await Assert.That(cascade).IsEmpty()
      .Because("a seed with no registered membership has nothing to announce to; it must be skipped, not crash the sweep");
  }
}
