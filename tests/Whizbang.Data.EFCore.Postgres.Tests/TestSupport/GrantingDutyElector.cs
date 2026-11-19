// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Startup;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestSupport;

/// <summary>
/// An elector that grants every duty at once, for a test whose subject is the work a holder does rather than the
/// election (#2044: the commit-order stamper leads only through a duty elector). Its grant carries no epoch, so the
/// fence passes, as it does for any elector that has no fencing token.
/// </summary>
internal sealed class GrantingDutyElector : IDutyElector {
  /// <summary>How many grants were handed out and later disposed.</summary>
  public int Released => Volatile.Read(ref _released);

  private int _released;

  /// <inheritdoc />
  public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
    Task.FromResult(DutyAttempt.Granted(new Grant(duty, this)));

  private sealed class Grant(string duty, GrantingDutyElector owner) : IDutyGrant {
    public string Duty => duty;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() {
      _ = Interlocked.Increment(ref owner._released);
      return ValueTask.CompletedTask;
    }
  }
}
