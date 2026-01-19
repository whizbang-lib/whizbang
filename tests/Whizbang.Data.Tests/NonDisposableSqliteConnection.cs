using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Wraps a SqliteConnection but prevents disposal.
/// Required for in-memory SQLite testing where the connection must remain open.
/// </summary>
public class NonDisposableSqliteConnection : IDbConnection {
  private readonly SqliteConnection _inner;

  public NonDisposableSqliteConnection(SqliteConnection inner) {
    ArgumentNullException.ThrowIfNull(inner);
    _inner = inner;
  }

  // Delegate all members to inner connection except Dispose
  // Explicit interface implementation to match IDbConnection's nullability contract
  // [AllowNull] matches IDbConnection.ConnectionString's annotation
  [AllowNull]
  string IDbConnection.ConnectionString {
    get => _inner.ConnectionString ?? string.Empty;
    set => _inner.ConnectionString = value ?? string.Empty;
  }

  // Public property delegates to interface implementation
  [AllowNull]
  public string ConnectionString {
    get => ((IDbConnection)this).ConnectionString;
    set => ((IDbConnection)this).ConnectionString = value;
  }

  public int ConnectionTimeout => _inner.ConnectionTimeout;
  public string Database => _inner.Database;
  public ConnectionState State => _inner.State;

  public IDbTransaction BeginTransaction() => _inner.BeginTransaction();
  public IDbTransaction BeginTransaction(IsolationLevel il) => _inner.BeginTransaction(il);
  public void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
  public void Close() { /* No-op - keep connection open for tests */ }
  public IDbCommand CreateCommand() => _inner.CreateCommand();

  public void Open() {
    // Only open if not already open
    if (_inner.State != ConnectionState.Open) {
      _inner.Open();
    }
  }

  // No-op - do NOT dispose the underlying connection
  public void Dispose() {
    // Intentionally empty - we want to keep the connection alive
    GC.SuppressFinalize(this);
  }
}
