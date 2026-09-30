using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A claim store written before claims could be read back or released still compiles and answers
/// honestly: it cannot tell which keys are held, and it releases nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/IClaimedEmissionStore.cs</code-under-test>
[Category("Unit")]
[Category("Dispatcher")]
public class ClaimedEmissionStoreDefaultsTests {

  [Test]
  public async Task FindClaimed_Default_CannotTellAsync() {
    IClaimedEmissionStore store = new ClaimOnlyStore();

    var found = await store.FindClaimedAsync(["k"], CancellationToken.None);

    await Assert.That(found).IsNull()
      .Because("null says the store cannot tell, as distinct from an empty set that says none of the keys is held");
  }

  [Test]
  public async Task Release_Default_ReleasesNothingAsync() {
    IClaimedEmissionStore store = new ClaimOnlyStore();

    await Assert.That(await store.ReleaseAsync("k", CancellationToken.None)).IsFalse();
  }

  [Test]
  public async Task Prune_Default_PrunesNothingAsync() {
    IClaimedEmissionStore store = new ClaimOnlyStore();

    await Assert.That(await store.PruneAsync("saga-completed:", DateTimeOffset.UtcNow, CancellationToken.None)).IsEqualTo(0);
  }

  private sealed class ClaimOnlyStore : IClaimedEmissionStore {
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken)
      => Task.FromResult(true);
  }
}
