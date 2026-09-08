using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The driver-side ring after a hot call (#720): it delivers what the call queued, reports how many,
/// and never throws, because a ring that fails is recovered by the next ring from any instance and by
/// the poll backstop, while an exception here would fail the commit the caller just made.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#doorbells-ring-after-commit</docs>
[Category("Integration")]
[Category("Shard2")]
[NotInParallel("EFCorePostgresTests")]
public class DoorbellRingerTests : EFCoreTestBase {
  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
  }

  private async Task<NpgsqlConnection> _openAsync() {
    // The base's own string; a connection taken from the context has its credentials stripped by Npgsql.
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  [Test]
  public async Task RingAsync_WithQueuedDoorbells_DeliversThemAndReturnsTheCountAsync() {
    await using var conn = await _openAsync();
    await using (var queue = conn.CreateCommand()) {
      queue.CommandText = "SELECT _queue_doorbell('wh_ringer_test', 'a'), _queue_doorbell('wh_ringer_test', 'b')";
      _ = await queue.ExecuteScalarAsync();
    }

    var rung = await DoorbellRinger.RingAsync(conn, "ring_doorbells", logger: null, CancellationToken.None);

    await Assert.That(rung).IsEqualTo(2)
      .Because("two distinct rings were queued; the ringer reports what it delivered");
    var again = await DoorbellRinger.RingAsync(conn, "ring_doorbells", logger: null, CancellationToken.None);
    await Assert.That(again).IsEqualTo(0)
      .Because("a ring drains the queue; nothing is delivered twice");
  }

  [Test]
  public async Task RingAsync_UnknownFunction_ReturnsMinusOneAndWarns_NeverThrowsAsync() {
    await using var conn = await _openAsync();
    var logger = new ListLogger();

    var rung = await DoorbellRinger.RingAsync(conn, "ring_doorbells_that_does_not_exist", logger, CancellationToken.None);

    await Assert.That(rung).IsEqualTo(-1)
      .Because("a failed ring is reported, not thrown: the caller's commit already happened and must not be failed by its doorbell");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("ring_doorbells_that_does_not_exist", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the failure names the function so an operator can see which ring is broken");
  }

  [Test]
  public async Task RingAsync_UnknownFunction_WithoutALogger_StillReturnsMinusOneAsync() {
    await using var conn = await _openAsync();

    var rung = await DoorbellRinger.RingAsync(conn, "ring_doorbells_that_does_not_exist", logger: null, CancellationToken.None);

    await Assert.That(rung).IsEqualTo(-1);
  }

  [Test]
  public async Task RingAsync_CanceledBeforeTheCall_ReturnsMinusOneWithoutLoggingAsync() {
    await using var conn = await _openAsync();
    var logger = new ListLogger();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var rung = await DoorbellRinger.RingAsync(conn, "ring_doorbells", logger, cts.Token);

    await Assert.That(rung).IsEqualTo(-1)
      .Because("shutdown is not a failure; the queued doorbells are rung by the next ring from any instance");
    await Assert.That(logger.Entries).IsEmpty()
      .Because("a cancellation at shutdown must not be logged as a broken ring");
  }
}
