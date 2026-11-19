// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Listens on one application topic's channel from a connection of its own and queues every payload in arrival order.
/// A test proves a notification was NOT delivered by sending a sentinel on the same channel from another connection:
/// notifications arrive in commit order, so if the one under test had been delivered it would be read before the sentinel.
/// </summary>
/// <remarks>
/// The probe's connections never enlist in an ambient <see cref="System.Transactions.TransactionScope"/>: a sentinel sent
/// inside the scope under test would otherwise be held back until that scope commits, and prove nothing.
/// </remarks>
internal sealed class AppSignalNotifyProbe : IAsyncDisposable {
  private readonly NpgsqlConnection _connection;
  private readonly string _connectionString;
  private readonly string _channel;
  private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
  private readonly CancellationTokenSource _stop = new();
  private Task _pump = Task.CompletedTask;

  private AppSignalNotifyProbe(string connectionString, string topic) {
    _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false }.ConnectionString;
    _channel = "wh_app_" + topic;
    _connection = new NpgsqlConnection(_connectionString);
  }

  /// <summary>Opens the probe and completes once its LISTEN is in force.</summary>
  public static async Task<AppSignalNotifyProbe> ListenAsync(string connectionString, string topic, CancellationToken cancellationToken) {
    var probe = new AppSignalNotifyProbe(connectionString, topic);
    await probe._connection.OpenAsync(cancellationToken);
    probe._connection.Notification += (_, e) => {
      if (e.Channel == probe._channel) {
        probe._received.Writer.TryWrite(e.Payload);
      }
    };
    await using (var listen = new NpgsqlCommand($"LISTEN \"{probe._channel}\"", probe._connection)) {
      await listen.ExecuteNonQueryAsync(cancellationToken);
    }
    probe._pump = probe._pumpAsync();
    return probe;
  }

  /// <summary>The next payload delivered on the channel.</summary>
  public async Task<string> NextAsync(CancellationToken cancellationToken) =>
    await _received.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

  /// <summary>Sends <paramref name="payload"/> on the channel from a connection outside any transaction.</summary>
  public async Task SendSentinelAsync(string payload, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var notify = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
    notify.Parameters.AddWithValue("channel", _channel);
    notify.Parameters.AddWithValue(nameof(payload), payload);
    _ = await notify.ExecuteScalarAsync(cancellationToken);
  }

  private async Task _pumpAsync() {
    try {
      while (!_stop.IsCancellationRequested) {
        // Notifications are raised only while the connection reads from the wire.
        await _connection.WaitAsync(_stop.Token);
      }
    } catch (OperationCanceledException) {
      // Stopped by DisposeAsync.
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await _stop.CancelAsync();
    await _pump;
    await _connection.DisposeAsync();
    _stop.Dispose();
  }
}
