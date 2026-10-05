// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A stored-form migration written as raw SQL, for a change to a perspective's stored documents that
/// <see cref="StoredFormAttribute"/> and <see cref="StoredFormRemovedAttribute"/> do not cover.
/// </summary>
/// <remarks>
/// Implement <see cref="IStoredFormMigration{TModel}"/>; this face is how the framework reads one.
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#custom</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormAttributeTests.cs</tests>
public interface IStoredFormMigration {
  /// <summary>
  /// The migration's stable name. The journal records the migration under it, so it runs once; a different name is
  /// a different migration. Unique within a DbContext.
  /// </summary>
  string Name { get; }

  /// <summary>
  /// Where the migration runs among the custom migrations of its table: a lower order runs first. Optional: a
  /// migration that does not state one has order 0. Custom migrations run after the generated migrations of the same
  /// table, and migrations with the same order run in the order of their classes' full names. The build reports
  /// WHIZ833 when two migrations of one table state the same order explicitly.
  /// </summary>
  /// <remarks>
  /// The build reads the order to place the migration, so a stated order must be a compile-time constant: a literal
  /// or a <see langword="const"/>, returned by an expression body, a getter that only returns it, or an initializer.
  /// A migration whose stated order the build cannot read is reported as WHIZ831 and does not run.
  /// </remarks>
  int Order => 0;

  /// <summary>
  /// The SQL to run against the perspective's table. It runs once, inside the stored-format rewrite phase's
  /// transaction under a savepoint of its own. To stop startup on data it cannot convert, raise
  /// <see cref="StoredFormMigrationTarget.BLOCKED_SQL_STATE"/> with a message naming what is wrong.
  /// </summary>
  /// <param name="target">The schema and table the migration runs against.</param>
  /// <returns>One or more SQL statements.</returns>
  string BuildSql(StoredFormMigrationTarget target);
}

/// <summary>
/// A stored-form migration of <typeparamref name="TModel"/>'s perspective table, written as raw SQL. The generator
/// finds implementations at build time (they need a parameterless constructor) and runs each once, journaled by its
/// <see cref="IStoredFormMigration.Name"/>, after the generated migrations of the same table, in its
/// <see cref="IStoredFormMigration.Order"/> and then by class name.
/// </summary>
/// <typeparam name="TModel">The perspective model whose stored documents the migration changes.</typeparam>
/// <docs>fundamentals/perspectives/stored-form-migrations#custom</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormAttributeTests.cs</tests>
/// <example>
/// <code>
/// public sealed class SplitFullName : IStoredFormMigration&lt;CustomerModel&gt; {
///   public string Name =&gt; "2026-10-customer-split-full-name";
///   public string BuildSql(StoredFormMigrationTarget target) =&gt;
///     $"UPDATE {target.QualifiedTable} SET data = data - 'FullName' WHERE data ? 'FullName'";
/// }
/// </code>
/// </example>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326:Unused type parameters should be removed", Justification = "The type argument is the declaration: it names the perspective table the generator binds the migration to.")]
public interface IStoredFormMigration<TModel> : IStoredFormMigration where TModel : class;

/// <summary>
/// The table a stored-form migration runs against.
/// </summary>
/// <param name="Schema">The schema, unquoted.</param>
/// <param name="Table">The perspective table, unquoted.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#custom</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormAttributeTests.cs</tests>
public readonly record struct StoredFormMigrationTarget(string Schema, string Table) {
  /// <summary>
  /// The SQLSTATE that stops startup: raised by a conversion that finds values it cannot convert, and by a custom
  /// migration that wants the same.
  /// </summary>
  public const string BLOCKED_SQL_STATE = "WH980";

  /// <summary>The schema-qualified table, each part quoted: <c>"schema"."table"</c>.</summary>
  public string QualifiedTable => $"{_quote(Schema)}.{_quote(Table)}";

  private static string _quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
