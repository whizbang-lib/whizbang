// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Which catalog extensions each registered database can use: one state per extension and database.
/// </summary>
/// <remarks>
/// Registered as a singleton by both Postgres drivers. The startup step probes once, after the
/// migration step; <see cref="RefreshAsync"/> may be called again later (for example by maintenance)
/// to pick up an operator's change without a restart. Until a database has been probed, and whenever it
/// cannot be read, its extensions read <see cref="PostgresExtensionState.Unknown"/>.
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilitiesTests.cs</tests>
public interface IPostgresCapabilities {
  /// <summary>The latest answer for every registered database, in registration order.</summary>
  IReadOnlyList<PostgresDatabaseExtensions> Databases { get; }

  /// <summary>One extension's state on one database.</summary>
  /// <param name="database">The database, as its driver registered it.</param>
  /// <param name="extension">The extension's name, compared without regard to case.</param>
  /// <returns>
  /// The state, or <see cref="PostgresExtensionState.Unknown"/> for a database or extension this
  /// instance does not know.
  /// </returns>
  PostgresExtensionState GetState(string database, string extension);

  /// <summary>Probes every registered database again. Never throws for a database it cannot read.</summary>
  /// <param name="cancellationToken">The caller's cancellation.</param>
  /// <returns>A task that completes when every database has its new answer.</returns>
  Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IPostgresCapabilities"/>: probes each registered database in turn with
/// <see cref="PostgresCapabilityProbe"/>, warns once per refresh for each database it could not read,
/// and publishes the <c>whizbang.postgres.extension.state</c> gauge when given metrics.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilitiesTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresCapabilityProbeIntegrationTests.cs</tests>
public sealed partial class PostgresCapabilities : IPostgresCapabilities {
  /// <summary>The reason a database reads as Unknown before its first probe.</summary>
#pragma warning disable CA1707
  public const string NOT_PROBED = "not probed yet";
#pragma warning restore CA1707

  private readonly PostgresExtensionProbeTarget[] _targets;
  private readonly IReadOnlyList<PostgresExtensionInfo> _catalog;
  private readonly ILogger<PostgresCapabilities> _logger;
  private volatile IReadOnlyList<PostgresDatabaseExtensions> _databases;

  /// <summary>Creates the capabilities over the databases to probe.</summary>
  /// <param name="targets">The databases; a name registered twice is probed once.</param>
  /// <param name="logger">Receives a warning for each database a probe could not read.</param>
  /// <param name="metrics">When given, the <c>Whizbang.Postgres</c> gauge is created over these capabilities.</param>
  /// <param name="catalog">The extensions to probe; <see cref="PostgresExtensionCatalog.BuiltIn"/> when null.</param>
  public PostgresCapabilities(
      IEnumerable<PostgresExtensionProbeTarget> targets,
      ILogger<PostgresCapabilities> logger,
      WhizbangMetrics? metrics = null,
      IReadOnlyList<PostgresExtensionInfo>? catalog = null) {
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(logger);
    _targets = [.. targets.DistinctBy(t => t.Database, StringComparer.Ordinal)];
    _catalog = catalog ?? PostgresExtensionCatalog.BuiltIn;
    _logger = logger;
    _databases = [.. _targets.Select(t => PostgresDatabaseExtensions.Unknown(t.Database, _catalog, NOT_PROBED))];
    Metrics = metrics is null ? null : new PostgresExtensionMetrics(metrics, this);
  }

  /// <summary>The gauge publishing these capabilities, or null when none was requested.</summary>
  public PostgresExtensionMetrics? Metrics { get; }

  /// <inheritdoc />
  public IReadOnlyList<PostgresDatabaseExtensions> Databases => _databases;

  /// <inheritdoc />
  public PostgresExtensionState GetState(string database, string extension) =>
    _databases.FirstOrDefault(d => d.Database == database)?
      .Extensions.FirstOrDefault(e => string.Equals(e.Extension.Name, extension, StringComparison.OrdinalIgnoreCase))?
      .State ?? PostgresExtensionState.Unknown;

  /// <inheritdoc />
  public async Task RefreshAsync(CancellationToken cancellationToken) {
    var results = new List<PostgresDatabaseExtensions>(_targets.Length);
    foreach (var target in _targets) {
      var result = await PostgresCapabilityProbe.ProbeAsync(target, _catalog, cancellationToken).ConfigureAwait(false);
      if (result.ProbeFailure is not null) {
        LogProbeFailed(_logger, result.Database, result.ProbeFailure);
      }
      results.Add(result);
    }
    _databases = results;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Postgres extensions on database '{Database}' could not be probed ({Reason}); every extension reads as Unknown until the next probe")]
  static partial void LogProbeFailed(ILogger logger, string database, string reason);
}
