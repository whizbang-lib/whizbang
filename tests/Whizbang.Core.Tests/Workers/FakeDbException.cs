using System.Data.Common;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A provider-neutral database exception carrying a SQLSTATE and a transient flag, the two things
/// every ADO.NET provider exposes through <see cref="DbException"/> and the only two the framework
/// reads when it classifies a failure.
/// </summary>
/// <remarks>
/// Build one with <see cref="WithSqlState"/>. The constructors are the standard exception ones and
/// set no SQLSTATE: were the SQLSTATE reachable through a single-string constructor, the form
/// one-string form would bind to the message instead, and a test built on an
/// exception whose SQLSTATE is silently null passes for the wrong reason.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The three standard constructors are here; the fourth the rule wants is the "
                + "serialization constructor, obsolete since .NET 8 (SYSLIB0051). A test double that "
                + "is thrown and caught in-process is never serialized.")]
internal sealed class FakeDbException : DbException {
  private readonly string? _sqlState;
  private readonly bool _isTransient;

  /// <summary>Creates one with no message, no SQLSTATE and no transient flag.</summary>
  public FakeDbException() { }

  /// <summary>Creates one with a message and no SQLSTATE.</summary>
  /// <param name="message">The message.</param>
  public FakeDbException(string message) : base(message) { }

  /// <summary>Creates one with a message, an inner exception and no SQLSTATE.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The wrapped exception.</param>
  public FakeDbException(string message, Exception innerException) : base(message, innerException) { }

  private FakeDbException(string? sqlState, bool isTransient, string? message, Exception? inner)
      : base(message ?? $"fake database failure {sqlState ?? "(no SQLSTATE)"}", inner) {
    _sqlState = sqlState;
    _isTransient = isTransient;
  }

  /// <summary>Creates one the way a provider would: a SQLSTATE, and optionally a transient flag.</summary>
  /// <param name="sqlState">The SQLSTATE the provider reports, or null for a provider that reports none.</param>
  /// <param name="isTransient">What the provider's own transient flag says.</param>
  /// <param name="message">The message; a generated one names the SQLSTATE when omitted.</param>
  /// <param name="inner">The wrapped exception, for the shapes a provider wraps.</param>
  /// <returns>The exception.</returns>
  internal static FakeDbException WithSqlState(
      string? sqlState, bool isTransient = false, string? message = null, Exception? inner = null) =>
    new(sqlState, isTransient, message, inner);

  public override string? SqlState => _sqlState;

  public override bool IsTransient => _isTransient;
}
