using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The grant's fencing token (#966). A grant that has one hands it to exclusive-work SQL; a grant
/// from the session-lock elector has none and says so with null rather than a made-up number,
/// so existing grant implementations keep compiling and keep meaning what they meant.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/IDutyElector.cs</code-under-test>
[Category("Startup")]
public class DutyGrantContractTests {

  private sealed class SessionLockStyleGrant : IDutyGrant {
    public string Duty => "maintainer";
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  [Test]
  public async Task Epoch_OfAGrantWithoutAFencingToken_IsNullAsync() {
    IDutyGrant grant = new SessionLockStyleGrant();

    await Assert.That(grant.Epoch).IsNull();
  }
}
