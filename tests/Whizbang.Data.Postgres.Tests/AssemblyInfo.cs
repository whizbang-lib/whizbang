using TUnit.Core;
using Whizbang.Data.Postgres.Tests;

// Limit concurrent Postgres tests to 15 to prevent overwhelming the system
// with too many PostgreSQL containers (each test gets its own container)
[assembly: ParallelLimiter<PostgresContainerLimit>]
