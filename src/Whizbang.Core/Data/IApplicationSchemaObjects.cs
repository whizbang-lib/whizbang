using System.Collections.Generic;

namespace Whizbang.Core.Data;

/// <summary>
/// SQL objects an application owns, and where in the framework's sequence they are applied.
/// </summary>
/// <remarks>
/// <para>
/// An application can always run its own SQL. What it cannot do on its own is say when: the
/// framework creates perspective tables and their indexes from its own schema pass, and an object
/// those depend on has to exist before that runs. An immutable function behind an expression index
/// is the case that forces this, since the function has to exist before the index that calls it and
/// the index has to exist before a query can use it.
/// </para>
/// <para>
/// So the contract here is the ordering, not the execution. <see cref="BeforePerspectives"/> is
/// applied after the framework's own migrations and before any perspective table or index exists;
/// <see cref="AfterPerspectives"/> is applied once they all do. Anything a perspective's schema
/// refers to belongs in the first; anything that refers to a perspective's table belongs in the
/// second.
/// </para>
/// <para>
/// Each object is applied once and then skipped while its SQL is unchanged, the same way the
/// framework's own migrations are, so contributing one costs nothing on later starts. Changing the
/// SQL of an object already applied re-applies it, which means the SQL has to be written so that
/// running it again is harmless.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ApplicationSchemaObjectsTests.cs</tests>
public interface IApplicationSchemaObjects {
  /// <summary>
  /// Objects that must exist before any perspective table or index is created.
  /// </summary>
  IReadOnlyList<ApplicationSchemaObject> BeforePerspectives { get; }

  /// <summary>
  /// Objects that may refer to perspective tables, and so are applied once those exist.
  /// </summary>
  IReadOnlyList<ApplicationSchemaObject> AfterPerspectives { get; }
}

/// <summary>
/// One SQL object an application owns.
/// </summary>
/// <param name="Name">
/// What the object is called in the framework's record of what it has applied. It identifies the
/// object across starts, so it has to stay the same when the SQL changes and has to differ between
/// objects.
/// </param>
/// <param name="Sql">
/// The statements that create it. Applied whole, and applied again whenever it changes, so it has
/// to be written to tolerate being run more than once.
/// </param>
/// <docs>operations/infrastructure/migrations</docs>
public sealed record ApplicationSchemaObject(string Name, string Sql);
