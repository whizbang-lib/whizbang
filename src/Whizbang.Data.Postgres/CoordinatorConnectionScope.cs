using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Disposable handle returned by <c>IWorkCoordinator</c> implementations'
/// per-call connection acquisition. Encapsulates the "pinned vs. fresh"
/// decision so each call site reads the same regardless of whether the
/// connection came from <see cref="PinnedConnectionContext.Current"/> or
/// was newly opened against the configured connection string / DbContext.
/// </summary>
/// <remarks>
/// <para>
/// The pool — NOT the coordinator — owns the lifetime of a pinned
/// connection. When this scope was constructed with
/// <c>ownsConnection: false</c>, <see cref="DisposeAsync"/> is a no-op:
/// closing or disposing the borrowed connection would corrupt the pool.
/// </para>
/// <para>
/// Reading <see cref="PinnedConnectionContext.Current"/> as
/// <see cref="NpgsqlConnection"/> is safe because the pinned pool only
/// ever stores Npgsql connections (the pool implementation is
/// PostgreSQL-bound). The cast cannot fail in practice; if it did, the
/// scope would simply fall back to opening a fresh connection.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public readonly struct CoordinatorConnectionScope : IAsyncDisposable {
  /// <summary>The acquired connection. Always open when the scope is returned.</summary>
  public NpgsqlConnection Connection { get; }
  private readonly bool _ownsConnection;

  internal CoordinatorConnectionScope(NpgsqlConnection connection, bool ownsConnection) {
    Connection = connection;
    _ownsConnection = ownsConnection;
  }

  /// <summary>
  /// Acquires a connection for a Dapper-style coordinator. Returns the pinned
  /// connection from <see cref="PinnedConnectionContext.Current"/> when one is
  /// in flight; otherwise constructs a fresh <see cref="NpgsqlConnection"/>
  /// against <paramref name="connectionString"/>.
  /// </summary>
  /// <param name="connectionString">The fall-back connection string used when no pinned connection is in scope.</param>
  /// <param name="cancellationToken">Caller cancellation; honoured during the open round-trip.</param>
  public static async ValueTask<CoordinatorConnectionScope> AcquireAsync(
      string connectionString, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connectionString);

    var pinned = PinnedConnectionContext.Current as NpgsqlConnection;
    if (pinned is not null) {
      if (pinned.State != ConnectionState.Open) {
        await pinned.OpenAsync(cancellationToken).ConfigureAwait(false);
      }
      return new CoordinatorConnectionScope(pinned, ownsConnection: false);
    }

    var fresh = new NpgsqlConnection(connectionString);
    await fresh.OpenAsync(cancellationToken).ConfigureAwait(false);
    return new CoordinatorConnectionScope(fresh, ownsConnection: true);
  }

  /// <summary>
  /// Acquires a connection for an EF Core-style coordinator. Prefers the
  /// pinned connection when one is in flight; otherwise pulls the
  /// DbContext's underlying connection (which the DbContext owns — the scope
  /// will NOT dispose it on the EF Core path).
  /// </summary>
  /// <param name="dbContextConnection">The DbContext's underlying connection (obtained via <c>DbContext.Database.GetDbConnection()</c>).</param>
  /// <param name="cancellationToken">Caller cancellation; honoured during the open round-trip.</param>
  public static async ValueTask<CoordinatorConnectionScope> AcquireForEfCoreAsync(
      NpgsqlConnection dbContextConnection, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContextConnection);

    var pinned = PinnedConnectionContext.Current as NpgsqlConnection;
    if (pinned is not null) {
      if (pinned.State != ConnectionState.Open) {
        await pinned.OpenAsync(cancellationToken).ConfigureAwait(false);
      }
      return new CoordinatorConnectionScope(pinned, ownsConnection: false);
    }

    if (dbContextConnection.State != ConnectionState.Open) {
      await dbContextConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
    // The DbContext owns the connection lifetime; scope does NOT dispose.
    return new CoordinatorConnectionScope(dbContextConnection, ownsConnection: false);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync()
    => _ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
