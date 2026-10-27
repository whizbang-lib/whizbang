// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1574: the durable-signal sweep takes its window from <see cref="WhizbangNotificationOptions.SignalRetention"/>
/// and deletes in batches, keeping its guards: never past the slowest pod's cursor.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus#retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
[NotInParallel("SignalRetention")]
public class PgDurableSignalRetentionWindowTests : EFCoreTestBase {

  private async Task<long> _signalAsync(string name, int ageHours) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_signals (wire_name, created_at) VALUES (@w, NOW() - make_interval(hours => @h)) RETURNING id", conn);
    cmd.Parameters.AddWithValue("w", name);
    cmd.Parameters.AddWithValue("h", ageHours);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private async Task _cursorAsync(long lastId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // The instance is registered: only a registered instance's cursor holds the sweep back (#1771).
    await using var cmd = new NpgsqlCommand(
      "DELETE FROM wh_signal_cursors; " +
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata) " +
      "VALUES (@id, 'utest-svc', 'utest-host', 1, NOW(), NOW(), '{}'::jsonb); " +
      "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id, updated_at) VALUES (@id, @last, NOW())", conn);
    cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
    cmd.Parameters.AddWithValue("last", lastId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<long> _remainingAsync(string prefix) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_signals WHERE wire_name LIKE @p", conn);
    cmd.Parameters.AddWithValue("p", prefix + "%");
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private PgDurableSignalRetentionWorker _worker(TimeSpan window, int batchSize = 5000) =>
    new(Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalRetention = window }),
      new ConfigurationBuilder().Build(), NullLogger<PgDurableSignalRetentionWorker>.Instance) { BatchSize = batchSize };

  [Test]
  public async Task Sweep_UsesTheConfiguredWindowAsync() {
    await _signalAsync("window-old", ageHours: 30);
    var young = await _signalAsync("window-young", ageHours: 12);
    await _cursorAsync(young);

    await _worker(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    await Assert.That(await _remainingAsync("window-old")).IsEqualTo(0L)
      .Because("a one-day window removes a signal a day and a half old, which the fixed seven days kept");
    await Assert.That(await _remainingAsync("window-young")).IsEqualTo(1L);
  }

  [Test]
  public async Task Sweep_DeletesInBatches_UntilNothingIsLeftAsync() {
    long last = 0;
    for (var i = 0; i < 5; i++) {
      last = await _signalAsync($"batch-{i}", ageHours: 48);
    }
    await _cursorAsync(last);

    var deleted = await _worker(TimeSpan.FromDays(1), batchSize: 2).SweepOnceAsync(CancellationToken.None);

    await Assert.That(await _remainingAsync("batch-")).IsEqualTo(0L)
      .Because("the sweep repeats its batch until a batch comes back short, so one cycle clears the whole backlog");
    await Assert.That(deleted).IsGreaterThanOrEqualTo(5);
  }

  [Test]
  public async Task Sweep_NeverPassesTheSlowestCursorAsync() {
    var read = await _signalAsync("cursor-read", ageHours: 48);
    await _signalAsync("cursor-unread", ageHours: 48);
    await _cursorAsync(read);

    await _worker(TimeSpan.FromDays(1), batchSize: 1).SweepOnceAsync(CancellationToken.None);

    await Assert.That(await _remainingAsync("cursor-unread")).IsEqualTo(1L)
      .Because("a signal no pod has delivered yet stays, however old, so a slow tail is never stranded");
  }
}
