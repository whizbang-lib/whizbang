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

    var delay = FirstDelay;
    for (var remaining = MAX_ATTEMPTS; ; remaining--) {
      try {
        await attempt().ConfigureAwait(false);
        return;
      } catch (Exception ex) when (alreadyDone(ex)) {
        // An earlier attempt did the work and lost its answer. Nothing left to do.
        return;
      } catch (Exception ex) when (remaining > 1 && IsContention(ex)) {
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

    var name = string.Create(CultureInfo.InvariantCulture, $"{namePrefix}_{Guid.NewGuid():N}");
    await RunWithRetryAsync(
      async () => {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      },
      IsAlreadyCreated,
      timeProvider ?? TimeProvider.System,
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
}
