// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  [Test]
  public async Task DrainRequested_OfAGrantThatCannotBeAsked_IsFalseAsync() {
    IDutyGrant grant = new SessionLockStyleGrant();

    await Assert.That(grant.DrainRequested).IsFalse();
  }

  private sealed class OneAtATimeElector : IDutyElector {
    public List<string> Asked { get; } = [];

    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Asked.Add(duty);
      return Task.FromResult(duty == "maintainer"
        ? DutyAttempt.Granted(new SessionLockStyleGrant())
        : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
    }
  }

  [Test]
  public async Task TryAcquireManyAsync_ByDefault_AsksOneAtATime_InOrderAsync() {
    var elector = new OneAtATimeElector();

    var attempts = await ((IDutyElector)elector).TryAcquireManyAsync(["maintainer", "commit-stamper"], CancellationToken.None);

    await Assert.That(elector.Asked).IsEquivalentTo(["maintainer", "commit-stamper"]);
    await Assert.That(attempts[0].Grant).IsNotNull();
    await Assert.That(attempts[1].Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(async () => await ((IDutyElector)elector).TryAcquireManyAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }
}
