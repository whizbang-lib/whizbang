// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Applies <c>Whizbang:Postgres</c> (the default for every database) and then
/// <c>Whizbang:Postgres:&lt;database&gt;</c> (that database's own values) to the named instance, or to
/// the unnamed instance for the first registered database. Reads the keys by hand (this assembly does not run the binder
/// source generator), so no reflection reaches the AOT path.
/// </summary>
/// <docs>operations/configuration/configuration-reference#postgres-databases</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresOptionsConfigurationTests.cs</tests>
internal sealed class PostgresOptionsPostConfigure(
  IConfiguration configuration,
  PostgresDefaultDatabase defaultDatabase,
  ILogger<PostgresOptionsPostConfigure>? logger = null)
  : IPostConfigureOptions<PostgresOptions> {

  private const string RETIRED_COMMAND_TIMEOUT_KEY = "CommandTimeoutSeconds";
  private int _retiredKeyReported;

  public void PostConfigure(string? name, PostgresOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var database = string.IsNullOrEmpty(name) ? defaultDatabase.Name : name;
    var root = configuration.GetSection(PostgresOptionsConfiguration.CONFIGURATION_SECTION);
    // A key directly under the section is the default for every database, so a service with one
    // database need not repeat its name; a key under the database's own name overrides it.
    _bind(root, options);
    _bind(root.GetSection(database), options);
    _reportRetiredKey(root, root.GetSection(database));
  }

  /// <summary>
  /// <c>CommandTimeoutSeconds</c> never reached a command. Rather than let a setting that does nothing
  /// pass silently, startup says once where a timeout is actually set.
  /// </summary>
  private void _reportRetiredKey(params IConfigurationSection[] sections) {
    foreach (var section in sections) {
      if (section[RETIRED_COMMAND_TIMEOUT_KEY] is not null
          && Interlocked.Exchange(ref _retiredKeyReported, 1) == 0
          && logger is not null) {
        PostgresOptionsLog.RetiredCommandTimeout(logger, ConfigurationPath.Combine(section.Path, RETIRED_COMMAND_TIMEOUT_KEY));
      }
    }
  }

  private static void _bind(IConfiguration section, PostgresOptions options) {
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.InitialRetryAttempts), v => options.InitialRetryAttempts = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(PostgresOptions.InitialRetryDelay), v => options.InitialRetryDelay = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(PostgresOptions.MaxRetryDelay), v => options.MaxRetryDelay = v);
    ConfigurationValueBinder.BindDouble(section, nameof(PostgresOptions.BackoffMultiplier), v => options.BackoffMultiplier = v);
    ConfigurationValueBinder.BindBool(section, nameof(PostgresOptions.RetryIndefinitely), v => options.RetryIndefinitely = v);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.MaxInFlightCommands), v => options.MaxInFlightCommands = v);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.CollectiveApplyBatchSize), v => options.CollectiveApplyBatchSize = v);
    ConfigurationValueBinder.BindInt(section, nameof(PostgresOptions.CollectiveApplyStatementTimeoutSeconds), v => options.CollectiveApplyStatementTimeoutSeconds = v);
  }
}

/// <summary>Source-generated logging for Postgres options binding.</summary>
internal static partial class PostgresOptionsLog {
  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "{Key} is set but has no effect: it is retired, and no command ever read it. Set "
              + "'Command Timeout' on the connection string the work runs on instead (the -init string "
              + "for schema initialization); the work coordinator keeps its own fixed budget")]
  public static partial void RetiredCommandTimeout(ILogger logger, string key);
}
