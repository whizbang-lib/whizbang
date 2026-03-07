using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// PostgreSQL database readiness check implementation.
/// Verifies database connectivity and presence of required Whizbang tables (inbox, outbox, eventstore).
/// </summary>
/// <docs>workers/database-readiness</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithRunningDatabaseAndSchema_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithInvalid_connectionString_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingTables_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_ReturnsConsistentResultAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingFunctions_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithAllRequiredFunctions_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithDataSourceFromPasswordProtectedConnection_AuthenticatesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:Constructor_WithNullDataSource_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithConnectionStringConstructor_MaintainsBackwardCompatibilityAsync</tests>
/// <remarks>
/// IMPORTANT: Prefer using the NpgsqlDataSource constructor over the connection string constructor.
/// NpgsqlDataSource.ConnectionString property strips passwords for security, so passing that stripped
/// string to this class will cause authentication failures. The NpgsqlDataSource constructor uses
/// CreateConnection() which properly retains credentials.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Health check logging - infrequent calls during startup and health checks")]
public class PostgresDatabaseReadinessCheck : IDatabaseReadinessCheck {
  private readonly NpgsqlDataSource? _dataSource;
  private readonly string? _connectionString;
  private readonly ILogger<PostgresDatabaseReadinessCheck> _logger;

  /// <summary>
  /// Creates a new PostgreSQL database readiness check using an NpgsqlDataSource.
  /// This is the preferred constructor as it properly retains credentials.
  /// </summary>
  /// <param name="dataSource">NpgsqlDataSource for creating connections</param>
  /// <param name="logger">Logger for diagnostic information</param>
  /// <remarks>
  /// Use this constructor when you have an NpgsqlDataSource available (e.g., from DI).
  /// This avoids the password stripping issue that occurs when extracting ConnectionString
  /// from the data source.
  /// </remarks>
  public PostgresDatabaseReadinessCheck(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDatabaseReadinessCheck> logger
  ) {
    _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    _connectionString = null;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Creates a new PostgreSQL database readiness check using a connection string.
  /// Maintained for backward compatibility with Dapper path.
  /// </summary>
  /// <param name="connectionString">PostgreSQL connection string (must include password)</param>
  /// <param name="logger">Logger for diagnostic information</param>
  /// <remarks>
  /// Use this constructor only when you have the full connection string including password.
  /// Do NOT pass NpgsqlDataSource.ConnectionString as it strips the password.
  /// </remarks>
  public PostgresDatabaseReadinessCheck(
    string connectionString,
    ILogger<PostgresDatabaseReadinessCheck> logger
  ) {
    _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    _dataSource = null;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Checks if the PostgreSQL database is ready.
  /// Returns true if:
  /// - Database connection can be established
  /// - Required Whizbang tables exist (inbox, outbox, eventstore)
  /// - Required Whizbang functions exist (process_work_batch, etc.)
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if database is ready, false otherwise</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithRunningDatabaseAndSchema_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithInvalid_connectionString_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingTables_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_ReturnsConsistentResultAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingFunctions_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithAllRequiredFunctions_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithDataSourceFromPasswordProtectedConnection_AuthenticatesSuccessfullyAsync</tests>
  public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    try {
      // Test database connectivity and table presence
      // Use DataSource.CreateConnection() when available (retains credentials)
      // Fall back to raw connection string for backward compatibility
      NpgsqlConnection connection;
      if (_dataSource != null) {
        connection = _dataSource.CreateConnection();
      } else {
        connection = new NpgsqlConnection(_connectionString);
      }
      await using var _ = connection;
      await connection.OpenAsync(cancellationToken);

      // Check for required Whizbang tables
      const string checkTablesSql = @"
        SELECT COUNT(*)
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name IN ('wh_inbox', 'wh_outbox', 'wh_event_store')";

      await using var tableCommand = new NpgsqlCommand(checkTablesSql, connection);
      var tableCount = Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

      if (tableCount < 3) {
        _logger.LogWarning(
          "PostgreSQL database not ready: Only {TableCount}/3 required tables found (wh_inbox, wh_outbox, wh_event_store)",
          tableCount
        );
        return false;
      }

      // Check for required Whizbang functions (installed by migrations)
      // process_work_batch is the critical function used by workers
      // Functions are installed in 'public' schema (via __SCHEMA__ placeholder replacement)
      const string checkFunctionsSql = @"
        SELECT COUNT(*)
        FROM information_schema.routines
        WHERE routine_schema = 'public'
          AND routine_name = 'process_work_batch'
          AND routine_type = 'FUNCTION'";

      await using var functionCommand = new NpgsqlCommand(checkFunctionsSql, connection);
      var functionCount = Convert.ToInt32(await functionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

      if (functionCount < 1) {
        _logger.LogWarning(
          "PostgreSQL database not ready: Required function 'task.process_work_batch' not found. Schema migrations may still be running."
        );
        return false;
      }

      _logger.LogDebug("PostgreSQL database ready: All required tables and functions present");
      return true;
    } catch (OperationCanceledException) {
      // Propagate cancellation
      throw;
    } catch (Exception ex) {
      _logger.LogWarning(
        ex,
        "PostgreSQL database not ready: {ErrorMessage}",
        ex.Message
      );
      return false;
    }
  }
}
