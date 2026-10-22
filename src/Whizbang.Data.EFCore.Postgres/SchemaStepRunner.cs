// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable S2325, S1144, CA1822, IDE0060, S1172 // RED stub, replaced by the implementation
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>How one step of the schema pass ended.</summary>
public enum SchemaStepOutcome {
  /// <summary>The step ran and committed.</summary>
  Applied,

  /// <summary>The step's ledger said it was already done, so nothing ran.</summary>
  AlreadyCurrent,

  /// <summary>Another instance brought the whole schema up to date while this one waited.</summary>
  CompletedElsewhere,
}

/// <summary>Runs one step of the schema pass in a transaction of its own, under the schema lock.</summary>
public sealed class SchemaStepRunner {
  /// <summary>Creates a runner.</summary>
  public SchemaStepRunner(
      DbContext dbContext, long lockId, string schema, Func<CancellationToken, Task<bool>> isSchemaCurrentAsync,
      IServiceProvider? services, ILogger? logger, TimeProvider? timeProvider = null,
      Func<Exception, TimeSpan?>? retryDelayFor = null) {
  }

  /// <summary>Starts numbering the pass's steps.</summary>
  public void BeginPass(int totalSteps) => throw new NotImplementedException();

  /// <summary>Counts a step the plan found current.</summary>
  public void Skip(string step) => throw new NotImplementedException();

  /// <summary>Logs the pass's summary.</summary>
  public void CompletePass() => throw new NotImplementedException();

  /// <summary>The SQLSTATE and message of a failure.</summary>
  public static string FailureText(Exception exception) => throw new NotImplementedException();

  /// <summary>Whether another instance finished the schema while this one waited.</summary>
  public bool CompletedElsewhere { get; private set; }

  /// <summary>Runs one step.</summary>
  public Task<SchemaStepOutcome> RunAsync(
      string step,
      Func<CancellationToken, Task<bool>> isCurrentAsync,
      Func<CancellationToken, Task> applyAsync,
      Func<Exception, CancellationToken, Task>? recordFailureAsync,
      CancellationToken cancellationToken) => throw new NotImplementedException();

  internal static TimeSpan LockRetryDelay(int attempt, Random random) => throw new NotImplementedException();
}
