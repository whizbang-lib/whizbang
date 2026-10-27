// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Releasing a session-level advisory lock says whether this session held it, and a release that found nothing to
/// release is logged: behind a pooler it means the lock is still held on some other backend.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/AdvisoryLockRelease.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class AdvisoryLockReleaseTests {
  private const long KEY = -6_120_034_871_225_119_533;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("lock_release");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task ReleasingALockThisSessionHolds_ReturnsTrue_AndLogsNothingAsync() {
    await using var session = await _openAsync();
    await _lockAsync(session);
    var logger = new ListLogger();

    var released = await AdvisoryLockRelease.ReleaseAsync(session, KEY, "test lock", logger, CancellationToken.None);

    await Assert.That(released).IsTrue();
    await Assert.That(logger.Warnings).IsEmpty();
    await using var other = await _openAsync();
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(other, KEY)).IsFalse();
  }

  [Test]
  public async Task ReleasingALockAnotherSessionHolds_ReturnsFalse_AndWarnsNamingTheLockAndKeyAsync() {
    await using var taker = await _openAsync();
    await _lockAsync(taker);
    await using var releaser = await _openAsync();
    var logger = new ListLogger();

    var released = await AdvisoryLockRelease.ReleaseAsync(releaser, KEY, "test lock", logger, CancellationToken.None);

    await Assert.That(released).IsFalse();
    await Assert.That(logger.Warnings).Count().IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("test lock");
    await Assert.That(logger.Warnings[0]).Contains(KEY.ToString(CultureInfo.InvariantCulture));
    await Assert.That(logger.Warnings[0]).Contains("not held");
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(releaser, KEY)).IsTrue()
      .Because("the release ran on a session that never took the lock, so the session that did still holds it");
  }

  private static async Task _lockAsync(NpgsqlConnection session) {
    await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({KEY})", session);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    return connection;
  }

  private sealed class ListLogger : ILogger {
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
