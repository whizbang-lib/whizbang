// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch backfill for <see cref="DutyHolderWorker"/>'s handler selection: a handler whose role this
/// instance does not manage (not in the role set, or role assignment switched off) is never held.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/DutyHolderWorker.cs</code-under-test>
[Category("Startup")]
public class DutyHolderWorkerBranchCoverageTests {

  private const string UNMANAGED_ROLE = "unmanaged-role";

  [Test]
  public async Task Roles_HandlerForARoleNotInTheRoleSet_IsIgnoredAsync() {
    var worker = _worker(new RoleAssignmentOptions(), [
      new Handler(StartupDuties.MAINTAINER, "Rewrite"),
      new Handler(UNMANAGED_ROLE, "Elsewhere"),
    ]);

    await Assert.That(worker.Roles).IsEquivalentTo([StartupDuties.MAINTAINER])
      .Because("a role this instance does not manage is held by assignment elsewhere, never by this loop");
    await Assert.That(worker.Holds(UNMANAGED_ROLE)).IsFalse();
  }

  [Test]
  public async Task Roles_RoleAssignmentDisabled_HoldsNothingAsync() {
    var worker = _worker(new RoleAssignmentOptions { Enabled = false }, [new Handler(StartupDuties.MAINTAINER, "Rewrite")]);

    await Assert.That(worker.Roles).IsEmpty()
      .Because("with role assignment switched off no role is managed, so no handler is taken up");
  }

  private static DutyHolderWorker _worker(RoleAssignmentOptions options, IEnumerable<IDutyWorkHandler> handlers) =>
    new(new UnusedElector(), new UnusedStore(), handlers, Options.Create(options), NullLogger<DutyHolderWorker>.Instance, notify: null);

  private sealed class Handler(string role, string key) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => key;
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }

  /// <summary>The constructor never consults the elector; any call would mean a pass ran.</summary>
  private sealed class UnusedElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no pass is run in these tests");
  }

  /// <summary>The constructor never consults the store; any call would mean a pass ran.</summary>
  private sealed class UnusedStore : IPendingDutyWorkStore {
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no pass is run in these tests");
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no pass is run in these tests");
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no pass is run in these tests");
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no pass is run in these tests");
  }
}
