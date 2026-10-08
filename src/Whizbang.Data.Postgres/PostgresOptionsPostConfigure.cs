// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Applies <c>Whizbang:Postgres:&lt;database&gt;</c> to the named instance, or to the unnamed instance
/// for the first registered database. Reads the keys by hand (this assembly does not run the binder
/// source generator), so no reflection reaches the AOT path.
/// </summary>
/// <docs>operations/configuration/configuration-reference#postgres-databases</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresOptionsConfigurationTests.cs</tests>
internal sealed partial class PostgresOptionsPostConfigure(
  IConfiguration configuration,
  PostgresDefaultDatabase defaultDatabase,
  ILogger<PostgresOptionsPostConfigure>? logger = null)
  : IPostConfigureOptions<PostgresOptions> {

  public void PostConfigure(string? name, PostgresOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var database = string.IsNullOrEmpty(name) ? defaultDatabase.Name : name;
    var section = configuration.GetSection(PostgresOptionsConfiguration.CONFIGURATION_SECTION).GetSection(database);

    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.InitialRetryAttempts), v => options.InitialRetryAttempts = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(PostgresOptions.InitialRetryDelay), v => options.InitialRetryDelay = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(PostgresOptions.MaxRetryDelay), v => options.MaxRetryDelay = v);
    ConfigurationValueBinder.BindDouble(section, nameof(PostgresOptions.BackoffMultiplier), v => options.BackoffMultiplier = v);
    ConfigurationValueBinder.BindBool(section, nameof(PostgresOptions.RetryIndefinitely), v => options.RetryIndefinitely = v);
    _bindRetiredCommandTimeout(section, database, options);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.MaxInFlightCommands), v => options.MaxInFlightCommands = v);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.CollectiveApplyBatchSize), v => options.CollectiveApplyBatchSize = v);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.CollectiveApplyStatementTimeoutSeconds), v => options.CollectiveApplyStatementTimeoutSeconds = v);
  }

  /// <summary>
  /// Binds the retired <c>CommandTimeoutSeconds</c> key and warns once per database that it does nothing.
  /// </summary>
  /// <remarks>
  /// The value is still assigned so code that reads the property keeps compiling, and so the warning can
  /// name the value the operator actually set. The alternative — dropping the key silently — is the state
  /// this replaces: an operator sets a documented timeout, no command reads it, and nothing says so.
  /// </remarks>
  private void _bindRetiredCommandTimeout(IConfigurationSection section, string database, PostgresOptions options) {
#pragma warning disable CS0618 // the key is retired; it is bound here only so the warning below can fire
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.CommandTimeoutSeconds), v => {
      options.CommandTimeoutSeconds = v;
      if (logger is not null) {
        LogRetiredCommandTimeout(logger, database, v, CoordinatorCommandBudget.SECONDS);
      }
    });
#pragma warning restore CS0618
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Whizbang:Postgres:{Database}:CommandTimeoutSeconds is set to {Configured}s and is retired: no "
      + "command reads it. Application queries take the connection string's Command Timeout, the work "
      + "coordinator uses its own fixed {CoordinatorBudget}s budget so a short application timeout cannot "
      + "cancel a commit batch, and schema initialization takes the '-init' connection string's Command "
      + "Timeout. Remove the key and set the timeout on the connection it applies to.")]
  private static partial void LogRetiredCommandTimeout(
    ILogger logger, string database, int configured, int coordinatorBudget);
}
