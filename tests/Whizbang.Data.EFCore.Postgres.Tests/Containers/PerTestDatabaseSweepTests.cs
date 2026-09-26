using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Containers;

/// <summary>
/// The sweep that clears the scratch databases a run left behind when it did not finish.
/// </summary>
/// <remarks>
/// <para>
/// A leftover database sounds like a disk problem and is not. A session still idle in a transaction
/// inside one of them holds a snapshot open; that keeps superseded row versions alive; and
/// <c>CREATE INDEX</c> then evaluates its expression over the values a migration has already
/// rewritten. The migration test that asserts the rewrite fails, complaining about the old
/// rendering, and nothing about the failure points at the database nobody dropped two days ago. It
/// happened on a developer's container that had accumulated 222 of them.
/// </para>
/// <para>
/// Every test here scopes the sweep to a prefix of its own, so one cannot claim another's database,
/// and drives age with a fake clock rather than by waiting an hour.
/// </para>
/// </remarks>
/// <docs>contributors/ai-agent-guide</docs>
[Category("Integration")]
[Category("Shard1")]
public class PerTestDatabaseSweepTests {
  /// <summary>Comfortably past the sweep's default window, so a database created now is stale.</summary>
  private static readonly TimeSpan PAST_THE_WINDOW = TimeSpan.FromHours(2);

  /// <summary>A clock reading <paramref name="ahead"/> later than now.</summary>
  /// <param name="ahead">How far into the future the sweep should think it is.</param>
  /// <returns>The clock.</returns>
  private static FakeTimeProvider _clockAhead(TimeSpan ahead) =>
    new(DateTimeOffset.UtcNow + ahead);

  /// <summary>Whether a database of that name is on the server.</summary>
  /// <param name="databaseName">The database name.</param>
  /// <returns><see langword="true"/> when it exists.</returns>
  private static async Task<bool> _existsAsync(string databaseName) {
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    // The parameter placeholder and the C# parameter deliberately do not share a name: matching
    // them reads as a nameof() that got away (RCS1015), and a SQL placeholder is not a symbol.
    await using var query = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", admin);
    _ = query.Parameters.AddWithValue("name", databaseName);
    return await query.ExecuteScalarAsync() is not null;
  }

  /// <summary>Creates a database under a name of the caller's choosing, bypassing the factory.</summary>
  /// <param name="databaseName">The name to create.</param>
  /// <returns>A task that completes when the database exists.</returns>
  private static async Task _createRawAsync(string databaseName) {
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin);
    _ = await create.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Production impact if this regresses: the leftovers accumulate silently on every long-lived
  /// container, and the first symptom is an unrelated migration test failing over a value a rewrite
  /// had already replaced.
  /// </summary>
  [Test]
  public async Task Sweep_DatabasesOlderThanTheWindow_AreDroppedAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var first = await PerTestDatabaseFactory.CreateAsync("sweepold");
    var second = await PerTestDatabaseFactory.CreateAsync("sweepold");

    var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
      namePrefix: "sweepold",
      staleAfter: TimeSpan.FromHours(1),
      timeProvider: _clockAhead(PAST_THE_WINDOW));

    await Assert.That(claimed).IsEqualTo(2);
    await Assert.That(await _existsAsync(first.Name)).IsFalse()
      .Because("a scratch database older than the window is what the sweep exists to remove");
    await Assert.That(await _existsAsync(second.Name)).IsFalse();
  }

  /// <summary>
  /// The guard that keeps the sweep from being dangerous: a database created moments ago belongs to
  /// a test that is still running, and dropping it would fail that test rather than tidy up.
  /// </summary>
  [Test]
  public async Task Sweep_ADatabaseInsideTheWindow_IsLeftAloneAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var live = await PerTestDatabaseFactory.CreateAsync("sweepfresh");

    try {
      var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
        namePrefix: "sweepfresh",
        staleAfter: TimeSpan.FromHours(1));

      await Assert.That(claimed).IsEqualTo(0);
      await Assert.That(await _existsAsync(live.Name)).IsTrue()
        .Because("a database this new belongs to a running test, not to a run that died");
    } finally {
      await PerTestDatabaseFactory.DropAsync(live.Name);
    }
  }

  /// <summary>
  /// An open connection does not save an abandoned database, because the connection is the part
  /// worth removing: it is the session holding the snapshot that breaks other tests.
  /// </summary>
  [Test]
  public async Task Sweep_AnAbandonedDatabaseWithASessionStillOpen_IsStillDroppedAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var abandoned = await PerTestDatabaseFactory.CreateAsync("sweepheld");

    await using var squatter = new NpgsqlConnection(abandoned.ConnectionString);
    await squatter.OpenAsync();
    await using var transaction = await squatter.BeginTransactionAsync();

    var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
      namePrefix: "sweepheld",
      staleAfter: TimeSpan.FromHours(1),
      timeProvider: _clockAhead(PAST_THE_WINDOW));

    await Assert.That(claimed).IsEqualTo(1);
    await Assert.That(await _existsAsync(abandoned.Name)).IsFalse()
      .Because("a session idle in a transaction is the reason the leftover matters, so it is closed with it");
  }

  /// <summary>
  /// A name that is not this factory's is not this sweep's business, even when it is the right shape
  /// and old enough by its digits. Guessing about an unrecognized name means dropping someone's
  /// database.
  /// </summary>
  [Test]
  public async Task Sweep_ANameWhoseStampIsNotADate_IsLeftAloneAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var impostor = $"sweepodd_99999999999999_{Guid.NewGuid():N}";
    await _createRawAsync(impostor);

    try {
      var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
        namePrefix: "sweepodd",
        staleAfter: TimeSpan.FromHours(1),
        timeProvider: _clockAhead(PAST_THE_WINDOW));

      await Assert.That(claimed).IsEqualTo(0);
      await Assert.That(await _existsAsync(impostor)).IsTrue()
        .Because("fourteen digits that are not a date were written by something else");
    } finally {
      await PerTestDatabaseFactory.DropAsync(impostor);
    }
  }

  /// <summary>
  /// Tidying never fails a run. A server that cannot be reached keeps its leftovers, and the caller
  /// gets a count of nothing rather than an exception at the start of every test session.
  /// </summary>
  [Test]
  public async Task Sweep_AServerThatCannotBeReached_ClaimsNothingAsync() {
    var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
      "Host=localhost;Port=1;Database=nothing;Username=nobody;Password=none;Timeout=1",
      namePrefix: null,
      staleAfter: null,
      timeProvider: null,
      cancellationToken: CancellationToken.None);

    await Assert.That(claimed).IsEqualTo(0);
  }

  /// <summary>
  /// The stamp costs fifteen characters of a name that has to stay inside an identifier, so a prefix
  /// long enough to push it over is refused where it is passed rather than truncated by the server
  /// into a name the drop afterwards cannot find.
  /// </summary>
  [Test]
  public async Task Create_APrefixTooLongForAnIdentifier_IsRefusedAsync() =>
    await Assert.That(async () => await PerTestDatabaseFactory.CreateAsync(new string('p', PerTestDatabaseFactory.MAX_PREFIX_LENGTH + 1)))
      .Throws<ArgumentException>()
      .Because("a truncated name would exist on the server under a name nothing else uses");

  /// <summary>
  /// Cancellation stops the sweep rather than propagating: whoever is shutting down is not waiting
  /// on housekeeping.
  /// </summary>
  [Test]
  public async Task Sweep_ACanceledToken_ClaimsNothingAsync() {
    await SharedPostgresContainer.InitializeAsync();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    var claimed = await PerTestDatabaseFactory.SweepAbandonedAsync(
      SharedPostgresContainer.ConnectionString,
      namePrefix: null,
      staleAfter: null,
      timeProvider: null,
      cancellationToken: canceled.Token);

    await Assert.That(claimed).IsEqualTo(0);
  }
}
