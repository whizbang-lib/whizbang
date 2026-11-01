// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The schema bootstrap closure failed for a reason that is not transient, so startup does not go on past Preflight
/// (#1636). <see cref="Step"/> names what failed: a bootstrap script, or one of the steps around them.
/// </summary>
/// <remarks>
/// A transient database failure (a deadlock, a dropped connection, a timeout) is retried by the bootstrap itself and
/// never ends up here. What does is a failure that would happen again on the next attempt: a script the server
/// rejects, a missing permission, a database that does not exist. Before #1636 those were logged at Warning and the
/// instance migrated under the schema lock alone, so a broken closure surfaced only as a missing election.
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ANonTransientFailure_FailsStartupLoudly_NamingWhatFailedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Schema/SchemaBootstrapRetryTests.cs:TheStandardConstructors_NameNoSchemaOrStepAsync</tests>
public sealed class SchemaBootstrapException : Exception {
  private const string UNKNOWN = "(unknown)";

  /// <summary>Creates the exception for a failed <paramref name="step"/> in <paramref name="schema"/>.</summary>
  /// <param name="schema">The schema whose bootstrap failed, unquoted.</param>
  /// <param name="step">What failed: a bootstrap script's name, or a step around the scripts in angle brackets.</param>
  /// <param name="innerException">Why it failed.</param>
  public SchemaBootstrapException(string schema, string step, Exception innerException)
    : base($"The schema bootstrap for {schema} failed at {step}, and the failure is not transient, so it is not retried: {innerException.Message}", innerException) {
    Schema = schema;
    Step = step;
  }

  /// <summary>Creates the exception with a message and a cause, naming no schema or step.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="innerException">Why it failed.</param>
  public SchemaBootstrapException(string message, Exception innerException) : base(message, innerException) {
    Schema = UNKNOWN;
    Step = UNKNOWN;
  }

  /// <summary>Creates the exception with a message, naming no schema or step.</summary>
  /// <param name="message">What failed.</param>
  public SchemaBootstrapException(string message) : base(message) {
    Schema = UNKNOWN;
    Step = UNKNOWN;
  }

  /// <summary>Creates the exception with no detail.</summary>
  public SchemaBootstrapException() : this("The schema bootstrap failed, and the failure is not transient.") { }

  /// <summary>The schema whose bootstrap failed, unquoted.</summary>
  public string Schema { get; }

  /// <summary>What failed: a bootstrap script's name, or a step around the scripts in angle brackets.</summary>
  public string Step { get; }
}
