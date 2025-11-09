using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IEventStore using Dapper.
/// </summary>
public class DapperSqliteEventStore : DapperEventStoreBase {
  public DapperSqliteEventStore(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

  protected override string GetAppendSql() => @"
    INSERT INTO whizbang_event_store (stream_key, sequence_number, envelope, created_at)
    VALUES (@StreamKey, @SequenceNumber, @Envelope, @CreatedAt)";

  protected override string GetReadSql() => @"
    SELECT envelope AS Envelope
    FROM whizbang_event_store
    WHERE stream_key = @StreamKey AND sequence_number >= @FromSequence
    ORDER BY sequence_number";

  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(sequence_number), -1)
    FROM whizbang_event_store
    WHERE stream_key = @StreamKey";
}
