using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that analyzes perspective tables whose expression indexes have no statistics.
/// </summary>
/// <remarks>
/// <para>
/// The schema pass analyzes a table as soon as it creates an index on it, but not every index reaches
/// the planner that way: one built outside the pass, a table whose post-pass <c>ANALYZE</c> failed, or a
/// start that died between its commit and its <c>ANALYZE</c>. Until the table is analyzed, a predicate on
/// the index's expression is estimated with a fixed default, and a selective predicate can be planned as a
/// scan of the whole table (issue #1004). This step finds such tables by their missing statistics and
/// analyzes them.
/// </para>
/// <para>
/// Bounded and done once per fleet. Each run analyzes at most a few tables, and only the instance that
/// wins the window's claim in <c>wh_unique_emission_claims</c> (the claim <c>PublishOnceAsync</c> uses)
/// runs at all; the others return at once. A host with no claim store skips the step rather than have
/// every instance analyze.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema this step looks in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <param name="maxTablesPerRun">The most tables one run analyzes.</param>
/// <docs>fundamentals/perspectives/perspective-indexes#statistics-for-a-new-index</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/IndexStatisticsMaintenanceStepTests.cs</tests>
public sealed partial class IndexStatisticsMaintenanceStep(
    Type dbContextType,
    ILogger<IndexStatisticsMaintenanceStep>? logger = null,
    TimeProvider? timeProvider = null,
    int maxTablesPerRun = IndexStatisticsMaintenanceStep.DEFAULT_MAX_TABLES_PER_RUN) : IMaintenanceStep {

  /// <summary>The most tables one run analyzes unless told otherwise.</summary>
  public const int DEFAULT_MAX_TABLES_PER_RUN = 5;

  /// <summary>How long one instance's claim to run the step lasts: the fleet runs it once per window.</summary>
  public static readonly TimeSpan ClaimWindow = TimeSpan.FromHours(1);

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "index-statistics";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var claims = services.GetService<IClaimedEmissionStore>();
    if (claims is null) {
      LogNoClaimStore(_logger);
      return;
    }

    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    schema = string.IsNullOrWhiteSpace(schema) ? "public" : schema;

    var now = _timeProvider.GetUtcNow();
    var window = now.UtcTicks - (now.UtcTicks % ClaimWindow.Ticks);
    var key = string.Create(CultureInfo.InvariantCulture, $"whizbang:index-statistics:{schema}:{window}");
    if (!await claims.TryClaimAsync(key, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)) {
      return;
    }

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      await IndexStatistics.AnalyzeMissingAsync(
        (NpgsqlConnection)context.Database.GetDbConnection(), PgIdentifier.Quote(schema), maxTablesPerRun,
        _logger, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Index-statistics step skipped: no claim store is registered to run it on one instance")]
  private static partial void LogNoClaimStore(ILogger logger);
}
