using TUnit.Core;

// Limit parallelism for integration tests to prevent resource exhaustion
// Each test creates 2 IHost instances with 6 background workers and database connections
// Running too many in parallel causes connection pool exhaustion and container resource limits
[assembly: MaxParallelTests(1)]
