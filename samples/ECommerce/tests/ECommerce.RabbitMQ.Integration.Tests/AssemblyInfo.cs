using TUnit.Core;

// Force all test classes to run sequentially (not in parallel) to prevent message stealing.
// Each test creates 2 IHost instances with 6 background workers and database connections
// Running too many in parallel causes connection pool exhaustion and container resource limits
[assembly: NotInParallel]
