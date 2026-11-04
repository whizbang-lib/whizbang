// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Builds a real <see cref="DutyHolderWorker"/> that manages one duty and either wins or loses its election, so a
/// test can put an instance on either side of the maintainer duty without a database.
/// </summary>
internal static class MaintainerDutyHolders {
  /// <summary>
  /// A holder that manages <paramref name="role"/> and has run one pass, so it holds the duty when
  /// <paramref name="grants"/> is true and does not otherwise.
  /// </summary>
  public static async Task<DutyHolderWorker> ElectedAsync(bool grants, string role = StartupDuties.MAINTAINER) {
    var holder = new DutyHolderWorker(new Elector(grants), new NoOwedWork(), [new Handler(role)], [],
      Options.Create(new RoleAssignmentOptions { Roles = { StartupDuties.MIGRATOR } }),
      NullLogger<DutyHolderWorker>.Instance, signalBus: null);
    await holder.RunOnceAsync(CancellationToken.None);
    return holder;
  }

  private sealed class Grant : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Elector(bool grants) : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(grants ? DutyAttempt.Granted(new Grant()) : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
  }

  private sealed class NoOwedWork : IPendingDutyWorkStore {
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<PendingDutyWork>>([]);
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
      Task.FromResult(DutyWorkCompletion.Completed);
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
      Task.FromResult(true);
  }

  private sealed class Handler(string role) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => "MaintainerDutyTest";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }
}
