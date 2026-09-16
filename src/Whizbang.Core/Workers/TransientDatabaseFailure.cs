using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace Whizbang.Core.Workers;

/// <summary>
/// A database failure that says something about the database at that moment and nothing about the
/// caller: a deadlock, a serialization failure, a statement the server canceled, a lock that timed
/// out, a connection that dropped, resources that ran out, or a command timeout the provider wrapped.
/// </summary>
/// <remarks>
/// <para>
/// Every long-running worker loop asks this before deciding what a thrown exception means. One that
/// passes is reported with its reason and the loop backs off and continues; one that does not is a
/// defect, reported as such, and the loop still continues, because a loop that ends takes the host
/// down with it under the default <c>BackgroundServiceExceptionBehavior</c>, and a fleet loses an
/// instance for the length of a restart over one deadlock against a sibling's schema DDL.
/// </para>
/// <para>
/// The classification reads only what every ADO.NET provider exposes through
/// <see cref="DbException"/>: the SQLSTATE and the provider's own transient flag. A timeout or a
/// socket failure counts only when it sits beneath a database exception; a wait that elapsed in
/// application code is not the database failing. Wrappers and aggregates are searched.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/transient-database-failures</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransientDatabaseFailureTests.cs</tests>
public sealed class TransientDatabaseFailure {
#pragma warning disable CA1707
  /// <summary>Two transactions each waited on a lock the other held; the server ended one.</summary>
  public const string DEADLOCK = "deadlock";
  /// <summary>A serializable or repeatable-read transaction could not be serialized.</summary>
  public const string SERIALIZATION_FAILURE = "serialization_failure";
  /// <summary>The server canceled the statement: a statement timeout or an explicit cancel.</summary>
  public const string STATEMENT_CANCELED = "statement_canceled";
  /// <summary>A lock could not be taken within the lock timeout.</summary>
  public const string LOCK_TIMEOUT = "lock_timeout";
  /// <summary>The connection was lost, refused, or the server is going away.</summary>
  public const string CONNECTION_LOST = "connection_lost";
  /// <summary>The server ran out of memory, disk, connections, or another resource.</summary>
  public const string INSUFFICIENT_RESOURCES = "insufficient_resources";
  /// <summary>The command's timeout elapsed on the client while waiting for the server.</summary>
  public const string COMMAND_TIMEOUT = "command_timeout";
  /// <summary>The provider marked the failure transient without a SQLSTATE this class names.</summary>
  public const string PROVIDER_TRANSIENT = "provider_transient";
#pragma warning restore CA1707

  private TransientDatabaseFailure(string reason, string? sqlState, Exception cause) {
    Reason = reason;
    SqlState = sqlState;
    Cause = cause;
  }

  /// <summary>Which kind of transient failure it is, one of the constants on this class.</summary>
  public string Reason { get; }

  /// <summary>The SQLSTATE the provider reported, when it reported one.</summary>
  public string? SqlState { get; }

  /// <summary>The database exception the classification was read from.</summary>
  public Exception Cause { get; }

  /// <summary>
  /// Classifies an exception as a transient database failure when one is anywhere in it.
  /// </summary>
  /// <param name="exception">The exception a loop caught, possibly wrapped.</param>
  /// <param name="failure">The classification, when <paramref name="exception"/> is one.</param>
  /// <returns><see langword="true"/> when a transient database failure was found.</returns>
  public static bool TryClassify(Exception exception, [NotNullWhen(true)] out TransientDatabaseFailure? failure) {
    ArgumentNullException.ThrowIfNull(exception);
    failure = _find(exception);
    return failure is not null;
  }

  /// <summary>Whether <paramref name="exception"/> carries a transient database failure.</summary>
  /// <param name="exception">The exception a loop caught, possibly wrapped.</param>
  /// <returns><see langword="true"/> when it does.</returns>
  public static bool IsTransient(Exception exception) => TryClassify(exception, out _);

  private static TransientDatabaseFailure? _find(Exception exception) {
    if (exception is AggregateException aggregate) {
      foreach (var inner in aggregate.InnerExceptions) {
        if (_find(inner) is { } found) {
          return found;
        }
      }
      return null;
    }

    if (exception is DbException db) {
      var classified = _classify(db);
      if (classified is not null) {
        return classified;
      }
    }

    return exception.InnerException is { } next ? _find(next) : null;
  }

  private static TransientDatabaseFailure? _classify(DbException db) {
    var state = db.SqlState;
    var reason = state switch {
      "40P01" => DEADLOCK,
      "40001" => SERIALIZATION_FAILURE,
      "57014" => STATEMENT_CANCELED,
      "55P03" => LOCK_TIMEOUT,
      "57P01" or "57P02" or "57P03" => CONNECTION_LOST,
      { } s when s.StartsWith("08", StringComparison.Ordinal) => CONNECTION_LOST,
      { } s when s.StartsWith("53", StringComparison.Ordinal) => INSUFFICIENT_RESOURCES,
      _ => null,
    };
    if (reason is not null) {
      return new TransientDatabaseFailure(reason, state, db);
    }

    // No SQLSTATE the class names. A timeout or a lost socket beneath the provider's exception is
    // the provider's own shape for a command timeout or a dropped connection.
    for (var inner = db.InnerException; inner is not null; inner = inner.InnerException) {
      if (inner is TimeoutException) {
        return new TransientDatabaseFailure(COMMAND_TIMEOUT, state, db);
      }
      if (inner is IOException or SocketException) {
        return new TransientDatabaseFailure(CONNECTION_LOST, state, db);
      }
    }

    return db.IsTransient ? new TransientDatabaseFailure(PROVIDER_TRANSIENT, state, db) : null;
  }
}
