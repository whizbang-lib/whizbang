using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That "is another instance still holding this?" can actually be answered, for the keys this
/// framework really computes.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL reports a single-bigint advisory key split in two, the high 32 bits in
/// <c>classid</c> and the low 32 in <c>objid</c>. A probe written the obvious way, comparing
/// <c>objid</c> to the key, is right for small positive keys and wrong for every key this framework
/// uses, because they are all full 64-bit hashes. Worse, it is wrong by aliasing: two unrelated
/// keys sharing a low half both read as held, so an instance would wait on a lock nobody holds.
/// Two of the tests here exist specifically to fail against that form.
/// </para>
/// <para>
/// Over a real server rather than a fake, because the thing under test is a catalog view's
/// representation of a lock. Nothing but PostgreSQL knows what that looks like.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class AdvisoryLockProbeTests {

  [Before(Test)]
  public async Task SetupAsync() => await SharedPostgresContainer.InitializeAsync();

  /// <summary>
  /// The session that asks the question.
  /// </summary>
  /// <remarks>
  /// Opened per test rather than held as a field, because the probe deliberately excludes the
  /// asking session and which session asks is therefore part of what each test arranges.
  /// </remarks>
  private static async Task<NpgsqlConnection> _askerAsync() {
    var asker = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await asker.OpenAsync();
    return asker;
  }

  /// <summary>A key unique to one test, so parallel work elsewhere cannot answer for it.</summary>
  private static long _freshKey() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);

  /// <summary>Opens another session and takes a session-scoped lock on it.</summary>
  private static async Task<NpgsqlConnection> _holdSessionLockAsync(long key) {
    var holder = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await holder.OpenAsync();
    await using var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", holder);
    take.Parameters.AddWithValue(key);
    await take.ExecuteScalarAsync();
    return holder;
  }

  private static async Task _unlockAsync(NpgsqlConnection connection, long key, CancellationToken ct) {
    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
    release.Parameters.AddWithValue(key);
    await release.ExecuteScalarAsync(ct);
  }

  /// <summary>A lock another session holds is visible.</summary>
  [Test]
  [Timeout(60000)]
  public async Task ALockAnotherSessionHoldsReadsAsHeldAsync(CancellationToken cancellationToken) {
    var key = _freshKey();
    await using var holder = await _holdSessionLockAsync(key);
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsTrue();
  }

  /// <summary>A released lock is not.</summary>
  /// <remarks>
  /// The half of the contract that decides a takeover. Reading a released lock as held is how a
  /// fleet waits forever on a migrator that already died.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task AReleasedLockReadsAsFreeAsync(CancellationToken cancellationToken) {
    var key = _freshKey();
    var holder = await _holdSessionLockAsync(key);
    await _unlockAsync(holder, key, cancellationToken);
    await holder.DisposeAsync();
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsFalse();
  }

  /// <summary>
  /// A transaction-scoped lock is visible for as long as its transaction is open.
  /// </summary>
  /// <remarks>
  /// The scope that actually matters: the schema initializer's migrator holds
  /// <c>pg_try_advisory_xact_lock</c>, not a session lock, so a probe that only saw session locks
  /// would report every working migrator as dead.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task ATransactionScopedLockReadsAsHeldAsync(CancellationToken cancellationToken) {
    var key = _freshKey();
    await using var asker = await _askerAsync();
    await using var holder = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var transaction = await holder.BeginTransactionAsync(cancellationToken);
    await using (var take = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", holder)) {
      take.Parameters.AddWithValue(key);
      await Assert.That(await take.ExecuteScalarAsync(cancellationToken) is true).IsTrue();
    }

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsTrue();

    await transaction.CommitAsync(cancellationToken);
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsFalse()
      .Because("committing is how a migrator signals it is done, and the lock goes with it");
  }

  /// <summary>
  /// The real schema key, which is negative, is found.
  /// </summary>
  /// <remarks>
  /// Not a synthetic edge case: <see cref="SchemaInitializationLockKey.Compute"/> returns a full
  /// 64-bit FNV-1a hash, so roughly half of all schema names produce a negative key and this is the
  /// one every deployment on the default schema uses. A probe comparing <c>objid</c> to the key
  /// finds nothing here, and every instance would conclude the migrator had died.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task TheRealSchemaKeyIsFoundEvenThoughItIsNegativeAsync(CancellationToken cancellationToken) {
    var key = SchemaInitializationLockKey.Compute("public");
    await Assert.That(key).IsLessThan(0)
      .Because("this test is only meaningful while the default schema's key needs both halves");

    await using var holder = await _holdSessionLockAsync(key);
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsTrue();
  }

  /// <summary>Holding one key does not answer for another.</summary>
  [Test]
  [Timeout(60000)]
  public async Task AnUnrelatedHeldKeyIsNotReportedAsync(CancellationToken cancellationToken) {
    var held = _freshKey();
    var asked = _freshKey();
    await using var holder = await _holdSessionLockAsync(held);
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, asked, cancellationToken))
      .IsFalse();
  }

  /// <summary>
  /// Two keys that differ only above the low 32 bits do not alias.
  /// </summary>
  /// <remarks>
  /// The precise defect a half-key comparison has. These two keys share their entire low half, so a
  /// probe on <c>objid</c> alone reports the wrong one as held, and two unrelated schemas in the
  /// same database is the ordinary case rather than a contrived one.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task KeysSharingALowHalfDoNotAliasAsync(CancellationToken cancellationToken) {
    const long LOW_HALF = 0x1234_5678L;
    var held = (0x0000_0001L << 32) | LOW_HALF;
    var asked = (0x0000_0002L << 32) | LOW_HALF;
    await using var holder = await _holdSessionLockAsync(held);
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, held, cancellationToken))
      .IsTrue();
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, asked, cancellationToken))
      .IsFalse()
      .Because("a low half is not a key; treating it as one makes an instance wait on nothing");
  }

  /// <summary>
  /// A two-integer advisory lock on the same numbers is not mistaken for a single-bigint one.
  /// </summary>
  /// <remarks>
  /// PostgreSQL has two advisory key shapes sharing one catalog representation, told apart only by
  /// <c>objsubid</c>. Every advisory-lock family in this framework uses the bigint form, but a
  /// consumer's own code is free to use the pair form and its lock must not read as ours.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task ATwoIntegerLockOnTheSameNumbersIsNotOursAsync(CancellationToken cancellationToken) {
    const int HIGH = 0x0000_4321;
    const int LOW = 0x0000_8765;
    var asked = ((long)HIGH << 32) | (uint)LOW;

    await using var holder = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1, $2)", holder)) {
      take.Parameters.AddWithValue(HIGH);
      take.Parameters.AddWithValue(LOW);
      await take.ExecuteScalarAsync(cancellationToken);
    }
    await using var asker = await _askerAsync();

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, asked, cancellationToken))
      .IsFalse();
  }

  /// <summary>
  /// A lock this very session holds is not "elsewhere".
  /// </summary>
  /// <remarks>
  /// The question is whether another instance is at work. An instance asking about a lock it holds
  /// itself has already decided it is the migrator, and answering yes would make a deferral wait on
  /// its own progress.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task ALockThisSessionHoldsIsNotElsewhereAsync(CancellationToken cancellationToken) {
    var key = _freshKey();
    await using var asker = await _askerAsync();
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", asker)) {
      take.Parameters.AddWithValue(key);
      await take.ExecuteScalarAsync(cancellationToken);
    }

    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(asker, key, cancellationToken))
      .IsFalse();

    await _unlockAsync(asker, key, cancellationToken);
  }

  /// <summary>A missing connection is a caller error.</summary>
  [Test]
  public async Task AMissingConnectionIsRefusedAsync() =>
    await Assert.That(async () => await AdvisoryLockProbe.IsHeldElsewhereAsync(null!, 1))
      .Throws<ArgumentNullException>();
}
