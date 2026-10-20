// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the generated initializer runs each migration with <c>search_path</c> set to the context's
/// schema, and restores it after.
/// </summary>
/// <remarks>
/// <para>
/// Shipped migrations are never edited, and several of their guards compare
/// <c>table_schema = current_schema()</c>; on an upgrade the ledger re-runs each one whose hash changed.
/// Setting the path per migration is what makes those guards ask about the schema they alter on a
/// context with a schema of its own. The helper is tested against a real database, and the replay over
/// a named schema shows the effect, but neither can say whether the generator emits the call around
/// every migration rather than once; this does.
/// </para>
/// <para>
/// Asserted on the emitted text, as the deferral wiring is, because what is at issue is the code the
/// generator produces.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <docs>contributors/data-engines/writing-migrations</docs>
public class MigrationSearchPathGenerationTests {
  private const string NAMED_SCHEMA_CONTEXT = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Happened : IEvent;

    public record PlainModel {
      [StreamId]
      public Guid ThingId { get; init; }

      public string Label { get; init; } = string.Empty;
    }

    public class PlainPerspective : IPerspectiveFor<PlainModel, Happened> {
      public PlainModel Apply(PlainModel currentData, Happened eventData) => currentData;
    }

    [WhizbangDbContext("named", Schema = "svc")]
    public class NamedDbContext : DbContext {
      public NamedDbContext(DbContextOptions<NamedDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _migrationBodyAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(NAMED_SCHEMA_CONTEXT);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
    var start = output.IndexOf("private static async Task _executeMigrationBodyAsync(", StringComparison.Ordinal);
    var end = output.IndexOf("private static async Task<long> _scalarRowCountAsync(", start, StringComparison.Ordinal);
    return output[start..end];
  }

  /// <summary>Every migration body runs between entering and leaving the context's search path.</summary>
  [Test]
  public async Task EachMigrationBody_RunsUnderTheContextsSearchPathAsync() {
    var body = await _migrationBodyAsync();

    var enter = body.IndexOf("MigrationSearchPath.EnterAsync(", StringComparison.Ordinal);
    var firstSegment = body.IndexOf("foreach (var segment in segments)", StringComparison.Ordinal);
    var exit = body.LastIndexOf("MigrationSearchPath.ExitAsync(", StringComparison.Ordinal);

    await Assert.That(enter).IsGreaterThan(-1)
      .Because("the path is set where every migration's segments are sent, so no migration runs without it");
    await Assert.That(enter).IsLessThan(firstSegment);
    await Assert.That(exit).IsGreaterThan(firstSegment)
      .Because("the path is restored after the migration, before the next one and the perspective DDL");
  }

  /// <summary>The path names the context's own schema.</summary>
  [Test]
  public async Task TheSearchPath_IsTheContextsSchemaAsync() {
    var body = await _migrationBodyAsync();

    await Assert.That(body).Contains("migrationConnection, \"svc\", cancellationToken", StringComparison.Ordinal)
      .Because("the schema substituted into the call is the one the context declares");
  }
}
