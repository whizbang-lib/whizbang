// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A deposit on a ledger whose books close.</summary>
public sealed record BooksProbeDeposited : IEvent {
  /// <summary>The ledger, which is the stream.</summary>
  [StreamId]
  public Guid LedgerId { get; init; }

  /// <summary>The amount.</summary>
  public decimal Amount { get; init; }
}

/// <summary>The closing summary of a ledger's period.</summary>
public sealed record BooksProbeClosed : IEvent {
  /// <summary>The ledger, which is the stream.</summary>
  [StreamId]
  public Guid LedgerId { get; init; }

  /// <summary>The balance carried forward.</summary>
  public decimal Balance { get; init; }
}

/// <summary>The opening balance of a ledger's next period.</summary>
public sealed record BooksProbeOpened : OpeningBalance {
  /// <summary>The balance carried forward.</summary>
  public decimal Balance { get; init; }
}

/// <summary>
/// Closing the books (#1741, WS-3 S12, migration 315), against a database the EF Core driver migrated: the summary, the seal
/// and the successor commit together, the successor's opening event names its predecessor and the predecessor's final
/// version, a failure opening the successor leaves the predecessor as it was, each stream that cannot be closed is refused
/// with nothing changed, and the sealed predecessor is archived after its grace and still readable.
/// </summary>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresBookCloser.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/315_CloseTheBooks.sql</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class CloseBooksSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

  private PostgresBookCloser _closer(TimeProvider? clock = null) =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, schema: null,
      new EFCoreDirectAppendRowBuilder(JsonContextRegistry.CreateCombinedOptions(), ephemeralModeResolver: null),
      clock ?? new FakeTimeProvider(_t0));

  private async Task<Guid> _ledgerAsync(int deposits) {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    for (var i = 0; i < deposits; i++) {
      await store.AppendAsync(stream, new BooksProbeDeposited { LedgerId = stream, Amount = 10 });
    }
    return stream;
  }

  [Test]
  public async Task CloseBooks_AppendsTheSummary_SealsTheStream_AndOpensTheSuccessorLinkedToItAsync() {
    var ledger = await _ledgerAsync(deposits: 3);

    var result = await _closer().CloseBooksAsync(ledger, new BooksProbeClosed { LedgerId = ledger, Balance = 30 },
      new BooksProbeOpened { Balance = 30, PredecessorStreamId = Guid.NewGuid(), PredecessorFinalVersion = 99 });

    await Assert.That(result.Status).IsEqualTo(BookCloseStatus.Closed);
    await Assert.That(result.PredecessorFinalVersion).IsEqualTo(4L).Because("the summary follows the three deposits");
    var successor = result.SuccessorStreamId!.Value;
    await Assert.That(successor).IsNotEqualTo(ledger);
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var opened = new List<BooksProbeOpened>();
    await foreach (var envelope in store.ReadAsync<BooksProbeOpened>(successor, 0)) {
      opened.Add(envelope.Payload);
    }
    await Assert.That(opened).Count().IsEqualTo(1).Because("the opening balance is the successor's first and only event");
    await Assert.That(opened[0].PredecessorStreamId).IsEqualTo(ledger)
      .Because("the link is the framework's: what the caller set is replaced by the stream whose books closed");
    await Assert.That(opened[0].PredecessorFinalVersion).IsEqualTo(4L);
    await Assert.That(opened[0].SuccessorStreamId).IsEqualTo(successor).Because("the opening event names the stream it opens");
    await Assert.That(opened[0].Balance).IsEqualTo(30m);
    var thrown = await Assert.That(async () => await store.AppendAsync(ledger, new BooksProbeDeposited { LedgerId = ledger, Amount = 1 }))
      .Throws<StreamSealedException>();
    await Assert.That(thrown!.Reason).Contains(successor.ToString()).Because("the seal says which stream continues it");
    await Assert.That(await _scalarAsync<long>("SELECT max(version)::bigint FROM wh_event_store WHERE stream_id = @s", ledger)).IsEqualTo(4L);
  }

  [Test]
  public async Task CloseBooks_AFailureOpeningTheSuccessor_LeavesThePredecessorUnsealed_AndStoresNothingAsync() {
    var ledger = await _ledgerAsync(deposits: 2);
    var closingId = (Guid)TrackedGuid.New();

    // Step (3) fails: the opening balance reuses the summary's event id, so its insert violates the event id's key.
    var failed = await Assert.That(async () => await _callAsync(ledger, expected: 2, closingId: closingId, openingId: closingId))
      .Throws<PostgresException>();

    await Assert.That(failed!.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ledger)).IsEqualTo(2L)
      .Because("the summary appended in step (1) rolls back with the failed step (3)");
    await Assert.That(await _scalarAsync<string>("SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ledger)).IsEqualTo("Active")
      .Because("the seal of step (2) rolls back too: the predecessor is as it was");
    await Assert.That(await _scalarAsync<bool>("SELECT sealed_at IS NULL FROM wh_stream_lifecycle WHERE stream_id = @s", ledger)).IsTrue();
    // The control: the same call with distinct ids closes the books.
    await Assert.That(await _callAsync(ledger, expected: 2, closingId: closingId, openingId: TrackedGuid.New())).IsEqualTo("closed");
  }

  [Test]
  public async Task CloseBooks_RefusesEachStreamItCannotClose_AndChangesNothingAsync() {
    var sealedLedger = await _ledgerAsync(deposits: 1);
    await _executeAsync("SELECT * FROM wh_finalize_stream(@s, 'ended', NOW())", sealedLedger);
    var archived = await _ledgerAsync(deposits: 1);
    await Assert.That((await new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync)
      .WriteAsync(ArchiveBatch.WholeStream(archived))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var changed = await _ledgerAsync(deposits: 2);
    var closer = _closer();

    var empty = await closer.CloseBooksAsync(Guid.Empty, new BooksProbeClosed(), new BooksProbeOpened());
    var never = await closer.CloseBooksAsync(TrackedGuid.New(), new BooksProbeClosed(), new BooksProbeOpened());
    var twice = await closer.CloseBooksAsync(sealedLedger, new BooksProbeClosed(), new BooksProbeOpened());
    var gone = await _callAsync(archived, expected: 1, closingId: TrackedGuid.New(), openingId: TrackedGuid.New());
    var raced = await _callAsync(changed, expected: 1, closingId: TrackedGuid.New(), openingId: TrackedGuid.New());
    var nothingInSql = await _callAsync(Guid.Empty, expected: 0, closingId: TrackedGuid.New(), openingId: TrackedGuid.New());

    await Assert.That(empty.Status).IsEqualTo(BookCloseStatus.NothingToClose);
    await Assert.That(never.Status).IsEqualTo(BookCloseStatus.NothingToClose).Because("a stream with no event has no books");
    await Assert.That(twice.Status).IsEqualTo(BookCloseStatus.AlreadySealed);
    await Assert.That(twice.SuccessorStreamId).IsNull();
    await Assert.That(gone).IsEqualTo("archived").Because("an archived stream is restored before its books can close");
    await Assert.That(raced).IsEqualTo("stream_changed")
      .Because("the caller read version 1 and the stream holds 2: the successor's link would be wrong, so nothing is written");
    await Assert.That(nothingInSql).IsEqualTo("nothing_to_close");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", changed)).IsEqualTo(2L);
    await Assert.That(await _scalarAsync<bool>("SELECT sealed_at IS NULL FROM wh_stream_lifecycle WHERE stream_id = @s", changed)).IsTrue();
  }

  [Test]
  public async Task CloseBooks_ThroughTheCloser_ReportsEachRefusalItsCallerCanActOnAsync() {
    var archived = await _ledgerAsync(deposits: 1);
    await new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).WriteAsync(ArchiveBatch.WholeStream(archived));
    var raced = await _ledgerAsync(deposits: 1);
    var racing = new RacingRows(new EFCoreDirectAppendRowBuilder(JsonContextRegistry.CreateCombinedOptions(), null), async () => {
      await using var context = CreateDbContext();
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(raced, new BooksProbeDeposited { LedgerId = raced });
    });
    var closer = new PostgresBookCloser(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, null, racing, new FakeTimeProvider(_t0));

    var onArchived = await _closer().CloseBooksAsync(archived, new BooksProbeClosed(), new BooksProbeOpened());
    var onRaced = await closer.CloseBooksAsync(raced, new BooksProbeClosed(), new BooksProbeOpened());

    await Assert.That(onArchived.Status).IsEqualTo(BookCloseStatus.Archived);
    await Assert.That(onRaced.Status).IsEqualTo(BookCloseStatus.StreamChanged)
      .Because("an append between the closer's read and the close is caught under the lock, and a retry closes the books");
    await Assert.That((await _closer().CloseBooksAsync(raced, new BooksProbeClosed(), new BooksProbeOpened())).Status).IsEqualTo(BookCloseStatus.Closed);
  }

  [Test]
  public async Task CloseBooks_WaitsForTheStreamsAppendLockAsync() {
    var ledger = await _ledgerAsync(deposits: 1);
    await using var appender = new NpgsqlConnection(ConnectionString);
    await appender.OpenAsync();
    await using var appending = await appender.BeginTransactionAsync();
    await using (var lockIt = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @s::text))", appender, appending)) {
      lockIt.Parameters.AddWithValue("s", ledger);
      _ = await lockIt.ExecuteScalarAsync();
    }

    var waited = await Assert.That(async () => await _callAsync(ledger, 1, TrackedGuid.New(), TrackedGuid.New(), lockTimeout: true))
      .Throws<PostgresException>();

    await Assert.That(waited!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
      .Because("the version is checked under the lock every append takes, so no append slips between the check and the summary");
    await appending.CommitAsync();
    await Assert.That(await _callAsync(ledger, 1, TrackedGuid.New(), TrackedGuid.New())).IsEqualTo("closed").Because("the control: free, it closes");
  }

  [Test]
  public async Task ThePredecessor_IsArchivedOnceItsGraceHasPassed_AndStaysReadableThroughTheArchiveAsync() {
    var ledger = await _ledgerAsync(deposits: 2);
    var result = await _closer().CloseBooksAsync(ledger, new BooksProbeClosed { LedgerId = ledger, Balance = 20 }, new BooksProbeOpened { Balance = 20 });
    var clock = new FakeTimeProvider(_t0);
    var archive = new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);
    var services = new ServiceCollection()
      .AddSingleton<IStreamDispositionLedger>(new PostgresStreamDispositionLedger(
        new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null)))
      .AddSingleton<IArchiveStore>(archive)
      .AddSingleton(Options.Create(new StreamLifecycleOptions { FinalizeGrace = TimeSpan.FromDays(7) }))
      .AddSingleton(Options.Create(new MaintenanceWorkerOptions()))
      .BuildServiceProvider();
    var step = new StreamDispositionStep(clock, NullLogger<StreamDispositionStep>.Instance);

    await step.RunAsync(services, CancellationToken.None);
    clock.Advance(TimeSpan.FromDays(7));
    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(BookCloseStatus.Closed);
    await Assert.That(await _scalarAsync<string>("SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ledger)).IsEqualTo("Archived")
      .Because("a closed book's predecessor is a sealed stream, disposed of by its type's action once its grace has passed");
    var archived = new List<ArchivedEvent>();
    await foreach (var e in archive.ReadAsync(ledger)) {
      archived.Add(e);
    }
    await Assert.That(archived).Count().IsEqualTo(3).Because("the deposits and the closing summary stay readable from the archive");
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheCloserAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("book-closer-registration"));
    _ = new Whizbang.Core.Perspectives.WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await Assert.That(services.Any(d => d.ServiceType == typeof(IBookCloser))).IsTrue()
      .Because("closing the books is turnkey on either driver");
  }

  // =================================================================================================

  // Calls wh_close_books directly, with two distinct deposit events.
  private async Task<string> _callAsync(Guid stream, long expected, Guid closingId, Guid openingId, bool lockTimeout = false) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();
    if (lockTimeout) {
      await using var set = new NpgsqlCommand("SET LOCAL lock_timeout = '100ms'", conn, tx);
      _ = await set.ExecuteNonQueryAsync();
    }
    const string type = "Whizbang.Data.EFCore.Postgres.Tests.BooksProbeDeposited";
    const string typeWithAssembly = type + ", Whizbang.Data.EFCore.Postgres.Tests";
    await using var c = new NpgsqlCommand(
      "SELECT close_status FROM wh_close_books(@p, @v, @ce, @t, @tt, '{}'::jsonb, '{}'::jsonb, NULL, 0, @succ, @oe, @t, @tt, '{}'::jsonb, '{}'::jsonb, NULL, 0, 'closed', @now)",
      conn, tx);
    c.Parameters.AddWithValue("p", stream);
    c.Parameters.AddWithValue("v", expected);
    c.Parameters.AddWithValue("ce", closingId);
    c.Parameters.AddWithValue("t", type);
    c.Parameters.AddWithValue("tt", typeWithAssembly);
    c.Parameters.AddWithValue("succ", (Guid)TrackedGuid.New());
    c.Parameters.AddWithValue("oe", openingId);
    c.Parameters.AddWithValue("now", _t0);
    var status = (string)(await c.ExecuteScalarAsync())!;
    await tx.CommitAsync();
    return status;
  }

  private async Task<T> _scalarAsync<T>(string sql, Guid stream) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var c = new NpgsqlCommand(sql, conn);
    c.Parameters.AddWithValue("s", stream);
    return (T)(await c.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql, Guid stream) => _ = await _scalarAsync<object>(sql, stream);

  // Appends to the stream the first time a row is built, between the closer's read of the newest version and its call.
  private sealed class RacingRows(IDirectAppendRowBuilder inner, Func<Task> race) : IDirectAppendRowBuilder {
    private Func<Task>? _race = race;

    public async ValueTask<DirectAppendRow> BuildAsync<TMessage>(TMessage payload, CancellationToken cancellationToken = default) where TMessage : notnull {
      if (Interlocked.Exchange(ref _race, null) is { } run) {
        await run();
      }
      return await inner.BuildAsync(payload, cancellationToken);
    }
  }
}
