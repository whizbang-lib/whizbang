using System.Data;
using Microsoft.Data.Sqlite;
using TUnit.Core;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Base class for Dapper integration tests.
/// Sets up an in-memory SQLite database with the Whizbang schema.
/// </summary>
public abstract class DapperTestBase : IDisposable, IAsyncDisposable {
  private static bool _typeHandlersRegistered = false;

  public SqliteConnection Connection { get; private set; } = null!;
  public DapperDbExecutor Executor { get; private set; } = null!;
  public SharedSqliteConnectionFactory ConnectionFactory { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    // Register Dapper type handlers once per assembly
    if (!_typeHandlersRegistered) {
      SqliteDateTimeOffsetHandler.Register();
      SqliteGuidHandler.Register();
      _typeHandlersRegistered = true;
    }
    // Create a new in-memory SQLite connection
    Connection = new SqliteConnection("Data Source=:memory:");
    Connection.Open();

    // Initialize executor and connection factory
    // IMPORTANT: For in-memory SQLite, we must reuse the same connection
    // because each new connection creates a fresh database without tables
    Executor = new DapperDbExecutor();
    ConnectionFactory = new SharedSqliteConnectionFactory(Connection);

    // Run SQL schema migration
    await InitializeDatabaseAsync();
  }

  [After(Test)]
  public void Cleanup() {
    try {
      if (Connection != null && Connection.State != ConnectionState.Closed) {
        Connection.Close();
      }
      Connection?.Dispose();
    } catch {
      // Swallow exceptions during cleanup to prevent masking test failures
    }
  }

  public void Dispose() {
    Connection?.Dispose();
    GC.SuppressFinalize(this);
  }

  public async ValueTask DisposeAsync() {
    if (Connection != null) {
      await Connection.DisposeAsync();
    }
    GC.SuppressFinalize(this);
  }

  private async Task InitializeDatabaseAsync() {
    // Read and execute SQLite migration script
    var schema = @"
-- Inbox table for message ingestion staging (receives from remote outbox)
-- Uses JSONB pattern with separate columns for event_type, event_data, metadata, scope
CREATE TABLE IF NOT EXISTS whizbang_inbox (
    message_id TEXT PRIMARY KEY,
    handler_name TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data TEXT NOT NULL,
    metadata TEXT NOT NULL,
    scope TEXT NULL,
    received_at TEXT NOT NULL DEFAULT (datetime('now')),
    processed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_processed_at ON whizbang_inbox(processed_at) WHERE processed_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_received_at ON whizbang_inbox(received_at);

-- Outbox table for transactional outbox pattern (ExactlyOnce sending)
-- Uses JSONB pattern with separate columns for event_type, event_data, metadata, scope
CREATE TABLE IF NOT EXISTS whizbang_outbox (
    message_id TEXT PRIMARY KEY,
    destination TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data TEXT NOT NULL,
    metadata TEXT NOT NULL,
    scope TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    published_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_published_at ON whizbang_outbox(published_at) WHERE published_at IS NULL;

-- Request/Response store for request-response pattern on pub/sub transports
CREATE TABLE IF NOT EXISTS whizbang_request_response (
    correlation_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL,
    response_envelope TEXT NULL,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_whizbang_request_response_expires_at ON whizbang_request_response(expires_at);

-- Event store for streaming/replay capability
CREATE TABLE IF NOT EXISTS whizbang_event_store (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    stream_id TEXT NOT NULL,
    sequence_number INTEGER NOT NULL,
    envelope TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    CONSTRAINT uq_whizbang_event_store_stream_sequence UNIQUE (stream_id, sequence_number)
);

CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_stream_id ON whizbang_event_store(stream_id, sequence_number);

-- Sequence provider for monotonic sequence generation
CREATE TABLE IF NOT EXISTS whizbang_sequences (
    sequence_key TEXT PRIMARY KEY,
    current_value INTEGER NOT NULL DEFAULT 0,
    last_updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
";

    using var command = Connection.CreateCommand();
    command.CommandText = schema;
    await command.ExecuteNonQueryAsync();
  }
}
