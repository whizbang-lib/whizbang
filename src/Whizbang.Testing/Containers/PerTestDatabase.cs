using System.Globalization;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.Containers;

/// <summary>A database created for one test, and the connection string that reaches it.</summary>
/// <param name="Name">The database name, for the drop that follows the test.</param>
/// <param name="ConnectionString">The string a fixture hands to a context or a connection.</param>
/// <docs>contributors/ai-agent-guide</docs>
public readonly record struct PerTestDatabase(string Name, string ConnectionString);

/// <summary>
/// Creates and drops the scratch database one test owns, retrying the contention that every
/// sibling fixture doing the same thing produces.
/// </summary>
/// <remarks>
/// <para>
/// Dozens of fixtures in one shard each create a database and drop it with <c>WITH (FORCE)</c>, all
/// against one container. <c>CREATE DATABASE</c> copies a template and takes a lock on it, so those
/// calls serialize, and a call that loses the race fails outright rather than waiting. The failure
/// lands in <c>[Before(Test)]</c>, which the runner reports as the test failing in a few
/// milliseconds, before a single assertion has run. That is a test-infrastructure race and not a
/// fact about the code under test, and it was diagnosed from exactly that shape: a test that cannot
/// physically reach its assertions in the time it took to fail.
/// </para>
/// <para>
/// So creation is retried with a bounded backoff. What counts as worth retrying is
/// <see cref="TransientDatabaseFailure"/>, the same classifier the worker loops use, rather than a
/// second hand-maintained list, plus the one state that is specific to creating a database:
/// <c>55006</c>, the template being read by another creator. That state is deliberately absent from
/// the production classifier, because a worker loop retrying "object in use" indefinitely is a
/// different decision from a fixture retrying for its own scratch database, and a test's needs do
/// not get to widen what production treats as transient.
/// </para>
/// <para>
/// The retry keeps one name across attempts, so an attempt that succeeded and then failed to report
/// is recognized: <c>42P04</c>, the database already existing, is success rather than an error. The
/// backoff runs on a <see cref="TimeProvider"/> so the policy can be driven in a test without
/// sleeping on a real clock.
/// </para>
/// </remarks>
/// <docs>contributors/ai-agent-guide</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Containers/PerTestDatabaseRetryTests.cs</tests>
public static class PerTestDatabaseFactory {
  /// <summary>How many times creation is attempted before the failure is the test's answer.</summary>
  internal const int MAX_ATTEMPTS = 5;

  /// <summary>The first wait between attempts; it doubles from here.</summary>
  internal static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(100);

  /// <summary>The template is being read by another creator: the contention this exists for.</summary>
  private const string OBJECT_IN_USE = "55006";

  /// <summary>The database is already there, which means an earlier attempt of ours won.</summary>
  private const string DUPLICATE_DATABASE = "42P04";

  /// <summary>How a scratch database's creation time is written into its name.</summary>
  private const string STAMP_FORMAT = "yyyyMMddHHmmss";

  /// <summary>
  /// The longest prefix that leaves room for the stamp and the GUID inside Postgres's 63-byte
  /// identifier limit: 63 less the stamp's fourteen, the GUID's thirty-two, and the two separators.
  /// </summary>
  internal const int MAX_PREFIX_LENGTH = 63 - 14 - 32 - 2;

  /// <summary>The shape <see cref="CreateAsync"/> gives a name: prefix, creation stamp, then a GUID.</summary>
  private const string SCRATCH_NAME_PATTERN = @"^[a-z0-9_]+_\d{14}_[0-9a-f]{32}$";

  /// <summary>The scratch databases on the server, whatever their age.</summary>
  private const string SCRATCH_DATABASES = """
    SELECT datname
    FROM pg_database
    WHERE NOT datistemplate
      AND datname ~ @pattern
      AND starts_with(datname, @prefix)
    """;

  /// <summary>
  /// How old a scratch database has to be before <see cref="SweepAbandonedAsync"/> claims it.
  /// </summary>
  /// <remarks>
  /// Far longer than one test holds its database and far shorter than the gap between two runs, so
  /// the sweep cannot mistake a live fixture's database for something left behind.
  /// </remarks>
  public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(1);

  /// <summary>
  /// Whether a failure while creating or dropping a scratch database is worth another attempt.
  /// </summary>
  /// <param name="exception">What the attempt threw.</param>
  /// <returns><see langword="true"/> when another attempt may succeed.</returns>
  internal static bool IsContention(Exception exception) =>
    TransientDatabaseFailure.IsTransient(exception)
    || (exception is PostgresException postgres && postgres.SqlState == OBJECT_IN_USE);

  /// <summary>Whether a create failed because an earlier attempt of ours had already succeeded.</summary>
  /// <param name="exception">What the attempt threw.</param>
  /// <returns><see langword="true"/> when the database is already there.</returns>
  internal static bool IsAlreadyCreated(Exception exception) =>
    exception is PostgresException { SqlState: DUPLICATE_DATABASE };

  /// <summary>
  /// Runs one attempt at a time until it succeeds, it is not contention, or the attempts run out.
  /// </summary>
  /// <param name="attempt">The statement to run.</param>
  /// <param name="alreadyDone">Reads a failure as "an earlier attempt of ours did this".</param>
  /// <param name="timeProvider">The clock the backoff waits on.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the work is done.</returns>
  internal static async Task RunWithRetryAsync(
      Func<Task> attempt,
      Func<Exception, bool> alreadyDone,
      TimeProvider timeProvider,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(attempt);
    ArgumentNullException.ThrowIfNull(alreadyDone);
    ArgumentNullException.ThrowIfNull(timeProvider);

    // The bound is in the loop's own condition rather than only in the catch filter below. Written
    // as an unbounded loop counting down, the one thing stopping it was a filter, so a later change
    // to what counts as contention would have turned this into a spin against a database that is
    // already struggling.
    var delay = FirstDelay;
    for (var attemptNumber = 1; attemptNumber <= MAX_ATTEMPTS; attemptNumber++) {
      try {
        await attempt().ConfigureAwait(false);
        return;
      } catch (Exception ex) when (alreadyDone(ex)) {
        // An earlier attempt did the work and lost its answer. Nothing left to do.
        return;
      } catch (Exception ex) when (attemptNumber < MAX_ATTEMPTS && IsContention(ex)) {
        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        delay *= 2;
      }
    }
  }

  /// <summary>
  /// Creates a database for one test and returns it with the string that reaches it.
  /// </summary>
  /// <param name="namePrefix">A short prefix naming the fixture, so a leftover database is traceable.</param>
  /// <param name="timeProvider">The clock the backoff waits on; the system clock by default.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The database and its connection string.</returns>
  /// <remarks>
  /// Call it instead of issuing <c>CREATE DATABASE</c> in a fixture. A fixture that issues its own
  /// is one more entrant in the race this exists to absorb.
  /// </remarks>
  public static async Task<PerTestDatabase> CreateAsync(
      string namePrefix,
      TimeProvider? timeProvider = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);
    if (namePrefix.Length > MAX_PREFIX_LENGTH) {
      // Said here rather than discovered later: Postgres truncates an over-long identifier silently,
      // so the database would exist under a name neither the connection string nor the drop uses.
      throw new ArgumentException(
        $"A scratch database prefix may be at most {MAX_PREFIX_LENGTH} characters so the name still "
        + $"fits an identifier; '{namePrefix}' is {namePrefix.Length}.",
        nameof(namePrefix));
    }

    // The creation time goes into the name because a database does not carry one: asking the server
    // means statting the database's directory, which only a superuser may do, and a sweep that needs
    // a privilege is a sweep that silently stops working. See SweepAbandonedAsync.
    var clock = timeProvider ?? TimeProvider.System;
    var stamp = clock.GetUtcNow().UtcDateTime.ToString(STAMP_FORMAT, CultureInfo.InvariantCulture);
    var name = string.Create(CultureInfo.InvariantCulture, $"{namePrefix}_{stamp}_{Guid.NewGuid():N}");
    await RunWithRetryAsync(
      async () => {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      },
      IsAlreadyCreated,
      clock,
      cancellationToken).ConfigureAwait(false);

    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = name,
      Timezone = "UTC",
    }.ConnectionString;

    return new PerTestDatabase(name, connectionString);
  }

  /// <summary>
  /// Drops the database a test owned, retrying the same contention, and never failing the test.
  /// </summary>
  /// <param name="name">The database name.</param>
  /// <param name="timeProvider">The clock the backoff waits on; the system clock by default.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the database is gone or the attempt is abandoned.</returns>
  /// <remarks>
  /// A database left behind costs nothing: the container goes with the run. So a drop that cannot
  /// win its race is abandoned rather than raised, because a teardown that throws turns a passing
  /// test red for tidying up.
  /// </remarks>
  public static async Task DropAsync(
      string name,
      TimeProvider? timeProvider = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);

    try {
      await RunWithRetryAsync(
        async () => {
          await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
          await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
          await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
          await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        },
        static _ => false,
        timeProvider ?? TimeProvider.System,
        cancellationToken).ConfigureAwait(false);
    } catch (NpgsqlException) {
      // Left behind deliberately: see the remarks.
    }
  }

  /// <summary>
  /// Drops the scratch databases an earlier run abandoned, so a long-lived container does not
  /// accumulate them.
  /// </summary>
  /// <param name="namePrefix">
  /// Limits the sweep to the databases one fixture creates. Omitted, every scratch database old
  /// enough is claimed.
  /// </param>
  /// <param name="staleAfter">How old a database must be to count as abandoned; an hour by default.</param>
  /// <param name="timeProvider">The clock the age is measured against; the system clock by default.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many databases were claimed.</returns>
  /// <remarks>
  /// <para>
  /// A fixture drops its own database when its test ends, so this is only ever about the runs that
  /// did not get to finish. In CI that costs nothing, because the container is discarded with the
  /// run. On a developer's machine the container outlives every run and the leftovers pile up, and
  /// they are not merely untidy: a session still idle in a transaction inside one of them holds a
  /// snapshot open, which keeps superseded row versions alive, which makes <c>CREATE INDEX</c>
  /// evaluate its expression over the values a migration already rewrote. A correct test then fails
  /// with an error about the old rendering. That is why the drop uses <c>WITH (FORCE)</c> rather
  /// than skipping a database that still has a connection: the connection is the thing worth
  /// removing.
  /// </para>
  /// <para>
  /// Age comes from the creation stamp in the name rather than from the server, so the sweep needs
  /// no privilege beyond the one the fixtures already use.
  /// </para>
  /// </remarks>
  public static Task<int> SweepAbandonedAsync(
      string? namePrefix = null,
      TimeSpan? staleAfter = null,
      TimeProvider? timeProvider = null,
      CancellationToken cancellationToken = default) =>
    SweepAbandonedAsync(
      SharedPostgresContainer.ConnectionString, namePrefix, staleAfter, timeProvider, cancellationToken);

  /// <summary>The sweep against a named server, so a test can point it somewhere of its own.</summary>
  /// <param name="connectionString">Where to sweep.</param>
  /// <param name="namePrefix">Limits the sweep to one fixture's databases, or null for all of them.</param>
  /// <param name="staleAfter">How old a database must be to count as abandoned.</param>
  /// <param name="timeProvider">The clock the age is measured against.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many databases were claimed.</returns>
  internal static async Task<int> SweepAbandonedAsync(
      string connectionString,
      string? namePrefix,
      TimeSpan? staleAfter,
      TimeProvider? timeProvider,
      CancellationToken cancellationToken) {
    var cutoff = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime
      - (staleAfter ?? DefaultStaleAfter);
    var claimed = 0;

    try {
      var abandoned = await _findScratchDatabasesAsync(
        connectionString, namePrefix, cutoff, cancellationToken).ConfigureAwait(false);

      foreach (var name in abandoned) {
        // The system clock, not the caller's: timeProvider says when "an hour ago" was, while the
        // backoff inside the drop has to wait on a clock that advances by itself. A fake one here
        // would leave the first contended drop waiting for a tick nobody is going to deliver.
        await DropAsync(name, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        claimed++;
      }
    } catch (NpgsqlException) {
      // Tidying is never worth failing a run over, so a server that cannot be read keeps its
      // leftovers. PostgresException is an NpgsqlException, so a refused statement lands here too.
    } catch (OperationCanceledException) {
      // Whoever is shutting down has better things to wait for.
    }

    return claimed;
  }

  /// <summary>Lists the scratch databases created before <paramref name="cutoff"/>.</summary>
  /// <param name="connectionString">Where to look.</param>
  /// <param name="namePrefix">One fixture's databases, or null for all of them.</param>
  /// <param name="cutoff">The moment a database has to predate to count.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The names, in no particular order.</returns>
  private static async Task<List<string>> _findScratchDatabasesAsync(
      string connectionString,
      string? namePrefix,
      DateTime cutoff,
      CancellationToken cancellationToken) {
    var found = new List<string>();

    await using var admin = new NpgsqlConnection(connectionString);
    await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var query = new NpgsqlCommand(SCRATCH_DATABASES, admin);
    _ = query.Parameters.AddWithValue("pattern", SCRATCH_NAME_PATTERN);
    _ = query.Parameters.AddWithValue("prefix", namePrefix is null ? string.Empty : namePrefix + "_");

    await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var name = reader.GetString(0);
      if (_createdBefore(name, cutoff)) {
        found.Add(name);
      }
    }

    return found;
  }

  /// <summary>Reads the creation stamp out of a name and says whether it predates the cutoff.</summary>
  /// <param name="name">A name of the shape <see cref="CreateAsync"/> produces.</param>
  /// <param name="cutoff">The moment to compare against.</param>
  /// <returns>
  /// <see langword="true"/> when the name carries a stamp older than the cutoff. A name whose stamp
  /// is fourteen digits but not a date is left alone: it was not written by this factory, and a
  /// sweep that guesses about a name it does not recognize is a sweep that drops someone's database.
  /// </returns>
  private static bool _createdBefore(string name, DateTime cutoff) {
    var stamp = name.Split('_')[^2];
    return DateTime.TryParseExact(
        stamp, STAMP_FORMAT, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
      && created < cutoff;
  }
}
