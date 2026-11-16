// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// The framework mechanism the current flow of work belongs to, so every database statement it runs is attributed to
/// it. A background worker's flow carries its mechanism from the moment the host starts it; a scope opened inside
/// that flow names a narrower one until it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// The value lives in an <see cref="AsyncLocal{T}"/>, so it follows the work through every <c>await</c> and into the
/// tasks the work starts, and never leaks into a flow that did not open it: disposing a scope restores what was
/// current when it was opened, and a child flow that opens its own scope changes nothing for its parent.
/// </para>
/// <para>
/// Nothing is written to the database to carry it. The statement hook reads <see cref="Current"/> when a command is
/// built and prepends <see cref="DbMechanism.SqlComment"/> to the command text, so attribution costs no extra
/// statement and survives a transaction-mode pooler, which a session setting would not.
/// </para>
/// </remarks>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/DbMechanismTests.cs</tests>
public static class DbMechanismScope {
  private static readonly AsyncLocal<DbMechanism?> _current = new();

  /// <summary>The mechanism the current flow belongs to, or <see langword="null"/> when none is open.</summary>
  public static DbMechanism? Current => _current.Value;

  /// <summary>
  /// Names <paramref name="mechanism"/> as the current flow's mechanism until the returned scope is disposed.
  /// </summary>
  /// <param name="mechanism">The mechanism the work inside the scope belongs to.</param>
  /// <returns>The scope; disposing it restores the mechanism that was current before.</returns>
  public static Scope Begin(DbMechanism mechanism) {
    ArgumentNullException.ThrowIfNull(mechanism);
    var previous = _current.Value;
    _current.Value = mechanism;
    return new Scope(previous);
  }

  /// <summary>An open mechanism scope; disposing it restores the mechanism that was current when it was opened.</summary>
  public sealed class Scope : IDisposable {
    private readonly DbMechanism? _previous;

    internal Scope(DbMechanism? previous) => _previous = previous;

    /// <summary>Restores the mechanism that was current when the scope was opened.</summary>
    public void Dispose() => _current.Value = _previous;
  }
}
