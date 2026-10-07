// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCorePostgresPerspectiveCheckpointCompleter"/>: a completion that
/// processed no event is skipped (logged at debug only when debug is enabled, and silently with no
/// logger), and the cursor table resolves to public for a model that maps no cursor entity, maps it
/// without a schema, or maps it under an explicit public schema.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveCheckpointCompleter.cs</code-under-test>
[Category("Shard2")]
public class CheckpointCompleterBranchTests : EFCoreTestBase {

  [Test]
  public async Task EmptyCompletion_IsSkipped_LoggedOnlyWhenDebugIsEnabledAsync() {
    var debug = new LevelLogger(Microsoft.Extensions.Logging.LogLevel.Debug);
    var info = new LevelLogger(Microsoft.Extensions.Logging.LogLevel.Information);

    await using (var ctx = CreateDbContext()) {
      await new EFCorePostgresPerspectiveCheckpointCompleter(ctx, debug).CompleteAsync([_emptyCompletion()]);
    }
    await using (var ctx = CreateDbContext()) {
      await new EFCorePostgresPerspectiveCheckpointCompleter(ctx, info).CompleteAsync([_emptyCompletion()]);
    }
    await using (var ctx = CreateDbContext()) {
      await new EFCorePostgresPerspectiveCheckpointCompleter(ctx).CompleteAsync([_emptyCompletion()]);
    }

    await Assert.That(debug.Messages.Any(m => m.Contains("skipped stream_id", StringComparison.Ordinal))).IsTrue();
    await Assert.That(info.Messages.Any(m => m.Contains("skipped stream_id", StringComparison.Ordinal))).IsFalse()
      .Because("the per-cursor skip line is debug-level and must not be formatted when debug is off");
    await Assert.That(info.Messages.Any(m => m.Contains("skipped 1 empty", StringComparison.Ordinal))).IsTrue();
    await Assert.That(await _cursorCountAsync()).IsEqualTo(0)
      .Because("a completion that processed nothing writes no cursor");
  }

  [Test]
  public async Task CursorTable_ResolvesToPublicForEveryModelShapeAsync() {
    var debug = new LevelLogger(Microsoft.Extensions.Logging.LogLevel.Debug);

    await using (var bare = new BareContext(new DbContextOptionsBuilder<BareContext>().UseNpgsql(ConnectionString).Options)) {
      await new EFCorePostgresPerspectiveCheckpointCompleter(bare, debug).CompleteAsync([_emptyCompletion()]);
    }
    await using (var explicitPublic = new ExplicitPublicContext(
        new DbContextOptionsBuilder<ExplicitPublicContext>().UseNpgsql(ConnectionString).Options)) {
      await new EFCorePostgresPerspectiveCheckpointCompleter(explicitPublic, debug).CompleteAsync([_emptyCompletion()]);
    }

    await Assert.That(debug.Messages.Count(m => m.Contains("skipped 1 empty", StringComparison.Ordinal))).IsEqualTo(2)
      .Because("both batches ran to completion against the public cursor table");
  }

  // ===== Helpers =====

  private static PerspectiveCursorCompletion _emptyCompletion() => new() {
    StreamId = Guid.CreateVersion7(),
    PerspectiveName = "BranchCoveragePerspective",
    LastEventId = Guid.Empty,
    ProcessedEventIds = [],
    Status = PerspectiveProcessingStatus.Completed,
  };

  private async Task<long> _cursorCountAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_perspective_cursors WHERE perspective_name = 'BranchCoveragePerspective'";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private sealed class LevelLogger(Microsoft.Extensions.Logging.LogLevel minimum)
      : Microsoft.Extensions.Logging.ILogger<EFCorePostgresPerspectiveCheckpointCompleter> {
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_messages) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= minimum;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (!IsEnabled(logLevel)) {
        return;
      }
      lock (_messages) {
        _messages.Add(formatter(state, exception));
      }
    }
  }

  /// <summary>A DbContext that maps none of the framework's entities.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);

  /// <summary>A DbContext that maps the framework's entities under an explicit public schema.</summary>
  public sealed class ExplicitPublicContext(DbContextOptions<ExplicitPublicContext> options) : DbContext(options) {
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema("public");
    }
  }
}
