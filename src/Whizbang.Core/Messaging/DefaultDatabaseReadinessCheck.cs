namespace Whizbang.Core.Messaging;

/// <summary>
/// Default database readiness check that always returns true.
/// Used when no specific database readiness implementation is provided.
/// </summary>
/// <remarks>
/// This implementation is appropriate for in-memory databases, test scenarios,
/// or when database connectivity is not a concern for the application.
/// For production scenarios with real databases (PostgreSQL, SQL Server, etc.),
/// provide a concrete implementation that verifies database connectivity and schema availability.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_AlwaysReturnsTrue_WithDefaultCancellationTokenAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_AlwaysReturnsTrue_WithCustomCancellationTokenAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_AlwaysReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancelledToken_StillReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_ImplementsInterface_IDatabaseReadinessCheckAsync</tests>
public class DefaultDatabaseReadinessCheck : IDatabaseReadinessCheck {
  /// <summary>
  /// Always returns true, indicating the database is ready.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_AlwaysReturnsTrue_WithDefaultCancellationTokenAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_AlwaysReturnsTrue_WithCustomCancellationTokenAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_AlwaysReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancelledToken_StillReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDatabaseReadinessCheckTests.cs:IsReadyAsync_ImplementsInterface_IDatabaseReadinessCheckAsync</tests>
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return Task.FromResult(true);
  }
}
