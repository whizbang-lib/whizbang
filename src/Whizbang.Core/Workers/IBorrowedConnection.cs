using System;
using System.Data.Common;

namespace Whizbang.Core.Workers;

/// <summary>
/// Disposable handle to a connection borrowed from <see cref="IPinnedConnectionPool"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Connection"/> is <c>null</c> when the pool is disabled, the
/// worker is ineligible, or <see cref="WhizbangPinnedPoolOptions.ConnectionString"/>
/// is not set. Workers branch on the null check rather than wrapping every
/// borrow site with conditional logic.
/// </para>
/// <para>
/// Dispose returns the connection (if any) to the pool. The pool — NOT the
/// caller — owns the connection's lifetime: callers must NOT call
/// <c>Close()</c> or <c>DisposeAsync()</c> on the borrowed connection directly.
/// </para>
/// <para>
/// The connection type is intentionally <see cref="DbConnection"/> (the
/// provider-agnostic base) rather than <c>NpgsqlConnection</c> so the
/// pool interface can live in <c>Whizbang.Core</c> without forcing every
/// Whizbang consumer to drag in the Npgsql package. The Postgres-specific
/// implementation is in <c>Whizbang.Data.Postgres</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedConnectionPoolPrimitivesTests.cs:NoOp_TryPin_ReturnsBorrowWithNullConnectionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PinnedConnectionPoolIntegrationTests.cs:RealPool_BorrowAndDispose_RoundTripsConnectionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PinnedConnectionPoolIntegrationTests.cs:RealPool_Size1_SecondBorrowBlocksUntilFirstDisposesAsync</tests>
public interface IBorrowedConnection : IAsyncDisposable {
  /// <summary>
  /// The pinned connection. <c>null</c> when the borrow is a no-op.
  /// </summary>
  DbConnection? Connection { get; }
}
