using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>What role this instance takes in migrating the schema.</summary>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
public enum SchemaStage {
  /// <summary>This instance holds the migrator duty and does the work.</summary>
  Migrator,

  /// <summary>Another instance holds it, so this one waits for the result.</summary>
  Waiter,

  /// <summary>
  /// No duty could be decided, so this instance migrates under the advisory lock alone, exactly as
  /// every instance did before an election existed.
  /// </summary>
  Unstaged,
}

/// <summary>The outcome of staging, and the duty if this instance won it.</summary>
/// <param name="Stage">What this instance does next.</param>
/// <param name="Grant">The held duty, when <paramref name="Stage"/> is
/// <see cref="SchemaStage.Migrator"/>. The caller owns it and must dispose it when the migration
/// ends, however it ends.</param>
/// <param name="Detail">Why, for logging.</param>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
public readonly record struct SchemaStaging(SchemaStage Stage, IDutyGrant? Grant, string Detail);

/// <summary>
/// Decides which instance migrates, once the bootstrap has made deciding possible.
/// </summary>
/// <remarks>
/// <para>
/// Registration comes first and is not optional. <c>record_capability</c> answers FALSE for an
/// instance that is not in the registry, so an election attempted before joining is refused for a
/// reason that has nothing to do with contention, and treating that refusal as meaningful is how
/// an earlier attempt at this turned every startup into a failure.
/// </para>
/// <para>
/// Which is the rule this type is built around: <b>no answer from the elector is fatal</b>. A
/// refusal, a missing function, a database that has never been migrated, an elector that is not
/// registered at all — every one of them ends with this instance unstaged, migrating under the
/// advisory lock that guarded this before any of it existed. Never migrating is a far worse failure
/// than migrating without a duty, and it is a failure an operator has to clear by hand.
/// </para>
/// <para>
/// The duty rides a session advisory lock on its own connection, so the holder's death releases it
/// with no timeout to tune, and a waiter watching that lock sees the takeover become available the
/// moment the holder stops existing.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorDutyStagingTests.cs</tests>
public static class MigratorDutyStaging {
  /// <summary>
  /// Registers this instance and contends for the migrator duty.
  /// </summary>
  /// <param name="elector">The elector, or <see langword="null"/> when none is registered.</param>
  /// <param name="registerAsync">Puts this instance in the registry. Must run before electing.</param>
  /// <param name="schema">The schema being staged, for reporting.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What this instance does next. Never throws for anything the elector reports.</returns>
  public static async Task<SchemaStaging> ElectAsync(
      IDutyElector? elector,
      Func<CancellationToken, Task> registerAsync,
      string schema,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(registerAsync);
    cancellationToken.ThrowIfCancellationRequested();

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;

    if (elector is null) {
      // A deployment that never wired the notification services. The lock is the only guard, as
      // it always was.
      MigratorDutyStagingLog.NoElector(log, schema);
      return new SchemaStaging(SchemaStage.Unstaged, null, "no elector is registered");
    }

    try {
      await registerAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Electing now would be refused anyway, and the refusal would say "unregistered" rather than
      // what actually went wrong. Report the real reason and fall back.
      MigratorDutyStagingLog.RegistrationFailed(log, ex, schema);
      return new SchemaStaging(SchemaStage.Unstaged, null, "this instance could not join the registry");
    }

    DutyAttempt attempt;
    try {
      attempt = await elector.TryAcquireAsync(StartupDuties.MIGRATOR, cancellationToken)
        .ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // A database with no capability function yet answers this way, and so does one where the
      // elector has no connection of its own. Either is a reason to stop electing, not to stop.
      MigratorDutyStagingLog.ElectorFailed(log, ex, schema);
      return new SchemaStaging(SchemaStage.Unstaged, null, "the elector could not be asked");
    }

    if (attempt.Grant is { } grant) {
      MigratorDutyStagingLog.Elected(log, schema);
      return new SchemaStaging(SchemaStage.Migrator, grant, "this instance holds the migrator duty");
    }

    var detail = attempt.Detail ?? "no detail";

    if (attempt.Refusal == DutyRefusal.Contended) {
      MigratorDutyStagingLog.Deferring(log, schema, detail);
      return new SchemaStaging(SchemaStage.Waiter, null, detail);
    }

    if (attempt.Refusal == DutyRefusal.Refused) {
      // Loud, because it means this instance is tombstoned or unknown and an operator should see
      // that. NOT fatal: refusing to migrate leaves the schema behind for everyone, and the lock
      // still stops two instances doing it at once.
      MigratorDutyStagingLog.Refused(log, schema, detail);
      return new SchemaStaging(SchemaStage.Unstaged, null, detail);
    }

    MigratorDutyStagingLog.Unavailable(log, schema, detail);
    return new SchemaStaging(SchemaStage.Unstaged, null, detail);
  }
}

/// <summary>Source-generated logging for migrator staging.</summary>
internal static partial class MigratorDutyStagingLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Information,
      Message = "This instance was elected to migrate schema {Schema}")]
  public static partial void Elected(ILogger logger, string schema);

  [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Information,
      Message = "Another instance holds the migrator duty for schema {Schema}, so this one will "
              + "wait for its result: {Detail}")]
  public static partial void Deferring(ILogger logger, string schema, string detail);

  [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "No duty elector is registered, so schema {Schema} will be migrated under the "
              + "advisory lock alone")]
  public static partial void NoElector(ILogger logger, string schema);

  [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Warning,
      Message = "This instance was refused the migrator duty for schema {Schema} and is evicted or "
              + "unknown to the registry; migrating under the advisory lock instead, because "
              + "leaving the schema behind would stop the whole fleet: {Detail}")]
  public static partial void Refused(ILogger logger, string schema, string detail);

  [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Warning,
      Message = "The migrator duty for schema {Schema} could not be decided, so this instance is "
              + "migrating under the advisory lock alone: {Detail}")]
  public static partial void Unavailable(ILogger logger, string schema, string detail);

  [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Warning,
      Message = "This instance could not join the registry for schema {Schema}, so no migrator duty "
              + "can be held; migrating under the advisory lock instead")]
  public static partial void RegistrationFailed(ILogger logger, Exception exception, string schema);

  [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Warning,
      Message = "The duty elector for schema {Schema} could not be asked, so this instance is "
              + "migrating under the advisory lock alone")]
  public static partial void ElectorFailed(ILogger logger, Exception exception, string schema);
}
