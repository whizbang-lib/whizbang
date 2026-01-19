using TUnit.Core.Interfaces;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Limits the number of concurrent Postgres tests to prevent resource exhaustion.
/// Each test spins up its own PostgreSQL container, so limiting parallelism
/// prevents overwhelming the system with too many containers.
/// </summary>
public sealed class PostgresContainerLimit : IParallelLimit {
  /// <summary>
  /// Maximum number of Postgres tests that can run concurrently.
  /// Set to 15 to balance parallelism with container resource usage.
  /// </summary>
  public int Limit => 15;
}
