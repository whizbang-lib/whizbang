// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>Duty-holder loops that hold, or do not hold, the maintainer duty, for steps gated on it.</summary>
internal static class DutyHolders {
  /// <summary>A loop that manages <paramref name="role"/> and has not won it.</summary>
  public static DutyHolderWorker NotHolding(string role = StartupDuties.MAINTAINER) => _holder(grants: false, role);

  /// <summary>A loop that wins the maintainer duty on its first pass.</summary>
  public static DutyHolderWorker Holding() => _holder(grants: true, StartupDuties.MAINTAINER);

  private static DutyHolderWorker _holder(bool grants, string role) =>
    new(new Elector(grants), new NoOwedWork(), [new Handler(role)], [], Options.Create(new RoleAssignmentOptions()),
      NullLogger<DutyHolderWorker>.Instance, notify: null);

  private sealed class Handler(string role) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => "PartitionMaintenanceTest";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }

  private sealed class Grant(string duty) : IDutyGrant {
    public string Duty => duty;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Elector(bool grants) : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(grants ? DutyAttempt.Granted(new Grant(duty)) : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
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
}
