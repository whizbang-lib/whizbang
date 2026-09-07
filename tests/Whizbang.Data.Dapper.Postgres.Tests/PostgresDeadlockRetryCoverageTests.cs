using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for the generic <see cref="PostgresDeadlockRetry.ExecuteAsync{T}"/>'s trailing
/// "Unreachable" guard. Every real transient-retry path returns or throws from inside the loop
/// body, so the only way to fall through to the guard is a <c>maxAttempts</c> that makes the loop
/// never execute at all (<c>attempt = 1 &lt;= maxAttempts</c> false from the start) — no
/// <see cref="Npgsql.PostgresException"/>, no database, needed to reach it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresDeadlockRetry.cs</code-under-test>
public class PostgresDeadlockRetryCoverageTests {

  // This line only exists because the compiler cannot see that the for-loop always returns or
  // throws for every attempt from 1..maxAttempts when maxAttempts >= 1. A caller passing
  // maxAttempts <= 0 (a config or call-site bug) must fail loudly with a clear signal rather than
  // silently return default(T) — silently returning a bogus default would let a caller believe a
  // database write succeeded when the operation never ran at all.
  [Test]
  public async Task ExecuteAsyncOfT_WithZeroMaxAttempts_ThrowsInvalidOperationExceptionAsync() {
    await Assert.That(() => PostgresDeadlockRetry.ExecuteAsync(
        async () => 1, maxAttempts: 0))
      .ThrowsExactly<InvalidOperationException>()
      .Because("maxAttempts <= 0 means the retry loop never runs a single attempt — falling "
             + "through to a default(T) return instead of this throw would silently tell the "
             + "caller the operation succeeded when it never executed");
  }
}
