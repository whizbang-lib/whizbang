// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The select-then-insert path's duplicate-key retry, all of its outcomes in one class. A save that loses
/// the insert race (Postgres 23505) once is retried and lands; one that keeps losing gives up after the
/// retry budget and surfaces the failure; a save that fails for any other reason is not retried at all.
/// The race is simulated by a save interceptor, on the in-memory provider the fallback path runs on. No
/// database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Unit")]
[Category("Shard2")]
public class BaseUpsertStrategyDuplicateKeyRetryTests {
  private const string TABLE = "wh_per_retry_model";

  [Test]
  public async Task Upsert_LosingTheInsertRaceOnce_RetriesAndPersistsTheRowAsync() {
    var interceptor = new FailingSaveInterceptor(failures: 1, duplicateKey: true);
    await using var context = _context(interceptor);
    var id = Guid.NewGuid();
    var recoveredBefore = BaseUpsertStrategy.DuplicateKeyRetriesRecovered;

    await new InMemoryUpsertStrategy().UpsertPerspectiveRowAsync(
      context, TABLE, id, new RetryModel { Name = "landed" }, _metadata(), new PerspectiveScope());

    await Assert.That(interceptor.Attempts).IsEqualTo(2).Because("the lost race is retried exactly once");
    await Assert.That(BaseUpsertStrategy.DuplicateKeyRetriesRecovered).IsGreaterThan(recoveredBefore);
    context.ChangeTracker.Clear();
    var row = await context.Set<PerspectiveRow<RetryModel>>().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.Name).IsEqualTo("landed");
  }

  [Test]
  public async Task Upsert_LosingTheInsertRaceEveryTime_GivesUpAfterTheRetryBudgetAsync() {
    var interceptor = new FailingSaveInterceptor(failures: int.MaxValue, duplicateKey: true);
    await using var context = _context(interceptor);

    await Assert.That(async () => await new InMemoryUpsertStrategy().UpsertPerspectiveRowAsync(
        context, TABLE, Guid.NewGuid(), new RetryModel { Name = "never" }, _metadata(), new PerspectiveScope()))
      .Throws<DbUpdateException>();
    await Assert.That(interceptor.Attempts).IsEqualTo(4)
      .Because("the first attempt and three retries, then the duplicate key reaches the caller's failure channel");
  }

  [Test]
  public async Task Upsert_FailingForAnotherReason_IsNotRetriedAsync() {
    var interceptor = new FailingSaveInterceptor(failures: int.MaxValue, duplicateKey: false);
    await using var context = _context(interceptor);

    await Assert.That(async () => await new InMemoryUpsertStrategy().UpsertPerspectiveRowAsync(
        context, TABLE, Guid.NewGuid(), new RetryModel { Name = "never" }, _metadata(), new PerspectiveScope()))
      .Throws<DbUpdateException>();
    await Assert.That(interceptor.Attempts).IsEqualTo(1)
      .Because("only a duplicate key is a race a second pass can win");
  }

  private static RetryDbContext _context(FailingSaveInterceptor interceptor) =>
    new(new DbContextOptionsBuilder<RetryDbContext>()
      .UseInMemoryDatabase($"upsert-retry-{Guid.NewGuid()}")
      .AddInterceptors(interceptor)
      .Options);

  private static PerspectiveMetadata _metadata() =>
    new() { EventType = "Test", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };

  private sealed class RetryModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed class RetryDbContext(DbContextOptions<RetryDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<RetryModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, d => d.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => {
          m.WithOwner();
          m.Property(x => x.EventType).IsRequired();
          m.Property(x => x.EventId).IsRequired();
          m.Property(x => x.Timestamp).IsRequired();
        });
        entity.Property(e => e.Scope)
          .HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  /// <summary>Fails the first <c>failures</c> saves, as a lost insert race (23505) or as some other failure.</summary>
  private sealed class FailingSaveInterceptor(int failures, bool duplicateKey) : SaveChangesInterceptor {
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) {
      var attempt = Interlocked.Increment(ref _attempts);
      if (attempt > failures) {
        return ValueTask.FromResult(result);
      }

      if (duplicateKey) {
        throw new DbUpdateException("save failed", new PostgresException(
          messageText: "duplicate key value violates unique constraint",
          severity: "ERROR", invariantSeverity: "ERROR", sqlState: "23505"));
      }
      throw new DbUpdateException("save failed", new InvalidOperationException("a failure no retry can fix"));
    }
  }
}
