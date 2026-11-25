// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Which form this process writes a strongly-typed id in inside a perspective document: the object
/// <c>{"Value": ...}</c> every release reads, or the UUID string a release from #2518 on reads (#2518).
/// </summary>
/// <remarks>
/// <para>
/// A release before #2518 maps an id in a document as a complex type, so its Entity Framework reader throws on a UUID
/// string. The UUID string is therefore written only once every live instance reads it: each instance announces that it
/// does (stored form level 2, <see cref="Whizbang.Core.Messaging.HeartbeatRequest.STORED_FORM_LEVEL"/>), and a schema's
/// gate latches once every live instance in it has. Until then this process writes the object form.
/// </para>
/// <para>
/// Each database schema the process writes documents to is held at startup until its gate is seen open
/// (<see cref="Hold"/>, <see cref="Open"/>). The process writes the UUID string once at least one schema is known and
/// every one it knows is open, and never before: a process that has not started a schema writes the object form. The
/// latch is one-way, so a schema once open stays open for the life of the process. A schema with no gate at all (its
/// registry or the gate function is missing) is not a fleet this process can wait on, so it is left out
/// (<see cref="Forget"/>) rather than holding every other schema for the life of the process.
/// </para>
/// <para>
/// Only the gate moves the process's form: the members that set it are internal, so consumer code cannot write the UUID
/// string ahead of the fleet. <c>StrongIdDocumentGate</c> in Whizbang.Data.Postgres is the public way in.
/// </para>
/// <para>
/// The process says which form it writes in its heartbeat, so the stored-form rewrite starts only once no live instance
/// can still write the object form (<see cref="Whizbang.Core.Messaging.HeartbeatRequest.STRONG_ID_DOCUMENT_FORM_KEY"/>).
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/WhizbangIdDocumentFormTests.cs</tests>
public static class WhizbangIdDocumentForm {
  private static readonly WhizbangIdDocumentFormState _process = new();
  private static readonly AsyncLocal<bool?> _scoped = new();

  /// <summary>
  /// Whether a document written here stores a strongly-typed id as its UUID string: the scoped choice when one is set
  /// (<see cref="ScopeTo"/>), otherwise the process's (<see cref="ProcessWritesUuidString"/>).
  /// </summary>
  public static bool WritesUuidString => _scoped.Value ?? _process.WritesUuidString;

  /// <summary>
  /// Whether this process writes the UUID string: at least one schema is known, and every schema known is open.
  /// </summary>
  public static bool ProcessWritesUuidString => _process.WritesUuidString;

  /// <summary>
  /// Records a schema whose gate is not known to be open, so the process keeps writing the object form until it is.
  /// A schema already open stays open.
  /// </summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  internal static void Hold(string? database, string? schema) => _process.Hold(database, schema);

  /// <summary>Records that a schema's gate is open: every live instance in it reads the UUID string.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  internal static void Open(string? database, string? schema) => _process.Open(database, schema);

  /// <summary>
  /// Leaves a schema out of the process's form: it has no gate (its registry or the gate function is missing), so there
  /// is no fleet in it to wait on. A schema already open stays open.
  /// </summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  internal static void Forget(string? database, string? schema) => _process.Forget(database, schema);

  /// <summary>Whether this process has seen the gate of a database schema open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <returns>True once its gate was seen open; false while held or never seen.</returns>
  internal static bool IsOpen(string? database, string? schema) => _process.IsOpen(database, schema);

  /// <summary>Whether this process holds a database schema: known, and its gate not seen open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <returns>True while held.</returns>
  internal static bool IsHeld(string? database, string? schema) => _process.IsHeld(database, schema);

  /// <summary>
  /// Sets the form for the current asynchronous flow, until the returned scope is disposed, whatever the process writes:
  /// for tests.
  /// </summary>
  /// <param name="uuidString">Whether to write the UUID string.</param>
  /// <returns>The scope; disposing it restores the form before.</returns>
  internal static IDisposable ScopeTo(bool uuidString) {
    var previous = _scoped.Value;
    _scoped.Value = uuidString;
    return new Scope(previous);
  }

  private sealed class Scope(bool? previous) : IDisposable {
    public void Dispose() => _scoped.Value = previous;
  }
}

/// <summary>The database schemas one process writes documents to, and whether each one's gate is open (#2518).</summary>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/WhizbangIdDocumentFormTests.cs</tests>
internal sealed class WhizbangIdDocumentFormState {
  /// <summary>The schema name a null or empty one stands for.</summary>
  private const string DEFAULT_SCHEMA = "public";

  private readonly Dictionary<string, bool> _schemas = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();
  private volatile bool _writesUuidString;

  /// <summary>At least one schema is known, and every schema known is open.</summary>
  public bool WritesUuidString => _writesUuidString;

  /// <summary>Records a schema not known to be open; one already open stays open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  public void Hold(string? database, string? schema) {
    lock (_gate) {
      _schemas.TryAdd(_key(database, schema), false);
      _recompute();
    }
  }

  /// <summary>Records that a schema is open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  public void Open(string? database, string? schema) {
    lock (_gate) {
      _schemas[_key(database, schema)] = true;
      _recompute();
    }
  }

  /// <summary>Leaves a schema that is not open out; one already open stays open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  public void Forget(string? database, string? schema) {
    lock (_gate) {
      var key = _key(database, schema);
      if (_schemas.TryGetValue(key, out var open) && !open) {
        _schemas.Remove(key);
        _recompute();
      }
    }
  }

  /// <summary>Whether a schema is known and not open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <returns>True while held.</returns>
  public bool IsHeld(string? database, string? schema) {
    lock (_gate) {
      return _schemas.TryGetValue(_key(database, schema), out var open) && !open;
    }
  }

  /// <summary>Whether a schema is known and open.</summary>
  /// <param name="database">The database the schema is in.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <returns>True once open.</returns>
  public bool IsOpen(string? database, string? schema) {
    lock (_gate) {
      return _schemas.TryGetValue(_key(database, schema), out var open) && open;
    }
  }

  private static string _key(string? database, string? schema) =>
    (database ?? string.Empty) + "/" + (string.IsNullOrEmpty(schema) ? DEFAULT_SCHEMA : schema);

  /// <summary>Called under <see cref="_gate"/>.</summary>
  private void _recompute() => _writesUuidString = _schemas.Count > 0 && _schemas.Values.All(open => open);
}
