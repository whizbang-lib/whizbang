// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The versions a stored-form conversion records as having applied it (#2131): the library version and the
/// application version as the instance records it.
/// </summary>
/// <remarks>
/// <para>
/// The rollback gate counts a conversion a newer version applied like any other change applied since an older one, so
/// every conversion that converts rows records who did: the stored-form migrations and the declared-key pass in
/// <c>wh_stored_form_migrations</c>, the canonical temporal rewrite in <c>wh_perspective_forms</c> (migration 385). A pass
/// that converts nothing keeps what is recorded.
/// </para>
/// <para>
/// The schema initializer sets it once, for its whole start (<see cref="ScopeTo"/>), so no conversion has to be handed
/// it: the rewrite phase carries it to every statement it runs as transaction settings
/// (<see cref="LIBRARY_SETTING"/>, <see cref="APPLICATION_SETTING"/>), and the declared-key pass reads it here. Outside
/// any scope a conversion records nobody, which never stands an instance down.
/// </para>
/// </remarks>
/// <param name="LibraryVersion">The library version.</param>
/// <param name="ApplicationVersion">The application version, as recorded (<c>unknown</c> when nobody knows it).</param>
/// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredFormApplierTests.cs</tests>
public sealed record StoredFormApplier(string LibraryVersion, string ApplicationVersion) {
  /// <summary>The transaction setting the rewrite phase carries the library version in.</summary>
  public const string LIBRARY_SETTING = "whizbang.applied_library_version";

  /// <summary>The transaction setting the rewrite phase carries the application version in.</summary>
  public const string APPLICATION_SETTING = "whizbang.applied_application_version";

  private static readonly AsyncLocal<StoredFormApplier?> _current = new();

  /// <summary>Who this flow of startup records as applying a conversion; null outside any scope.</summary>
  public static StoredFormApplier? Current => _current.Value;

  /// <summary>
  /// Records <paramref name="applier"/> as the applier of every conversion in this flow until the returned scope is
  /// disposed, then whoever was recorded before.
  /// </summary>
  /// <param name="applier">The versions to record.</param>
  /// <returns>The scope.</returns>
  /// <remarks>Called from the initializer's own body, not from an async method, so it reaches every phase it then
  /// awaits.</remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredFormApplierTests.cs:ScopeTo_RestoresTheApplierBefore_AndRefusesNoneAsync</tests>
  public static IDisposable ScopeTo(StoredFormApplier applier) {
    ArgumentNullException.ThrowIfNull(applier);
    var previous = _current.Value;
    _current.Value = applier;
    return new Scope(previous);
  }

  /// <summary>
  /// The PL/pgSQL statement a conversion runs when it applied something: records the applier the rewrite phase
  /// carried on <paramref name="journal"/>'s row where <paramref name="keyColumn"/> is <paramref name="keyLiteral"/>.
  /// Nothing on a journal older than 385.
  /// </summary>
  /// <param name="journal">The journal, schema-qualified.</param>
  /// <param name="keyColumn">Its key column.</param>
  /// <param name="keyLiteral">The key, as a SQL literal.</param>
  /// <param name="indent">The leading spaces of each line.</param>
  /// <returns>The statement.</returns>
  internal static string RecordSql(string journal, string keyColumn, string keyLiteral, string indent) =>
    $"""
    {indent}IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass({SqlText.Literal(journal)})
    {indent}           AND attname = 'applied_library_version' AND NOT attisdropped) THEN
    {indent}  UPDATE {journal} SET applied_library_version = nullif(current_setting('{LIBRARY_SETTING}', true), ''),
    {indent}    applied_application_version = nullif(current_setting('{APPLICATION_SETTING}', true), '')
    {indent}  WHERE {keyColumn} = {keyLiteral};
    {indent}END IF;

    """;

  /// <summary>Restores the applier recorded before.</summary>
  private sealed class Scope(StoredFormApplier? previous) : IDisposable {
    public void Dispose() => _current.Value = previous;
  }
}
