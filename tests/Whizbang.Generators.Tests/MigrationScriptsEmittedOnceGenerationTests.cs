// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the embedded migration set is emitted once per assembly, however many DbContexts the assembly declares.
/// </summary>
/// <remarks>
/// <para>
/// The migration set is several megabytes of SQL and grows with every release. It used to be emitted into every
/// DbContext's schema extensions, so an assembly with thirteen contexts compiled thirteen identical copies: about
/// 120 MB of generated source, nine times its hand-written code, and a compile that outgrew a 16 GB build runner
/// (#2388). The text is the same for every context (the schema is substituted at run time), so one copy serves
/// them all.
/// </para>
/// <para>
/// What the shared copy decodes to, and that it is emitted as UTF-8 literals, is pinned by
/// <see cref="MigrationUtf8LiteralGenerationTests"/>; that is what keeps every recorded migration hash unchanged.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/MigrationScriptsTemplate.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
public class MigrationScriptsEmittedOnceGenerationTests {
  // Two contexts in two namespaces: the shared class lives in one of them, so the other has to reach it by its full name.
  private const string TWO_CONTEXTS = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp.Orders {
      public record Happened : IEvent;

      public record PlainModel {
        [StreamId]
        public Guid ThingId { get; init; }
      }

      public class PlainPerspective : IPerspectiveFor<PlainModel, Happened> {
        public PlainModel Apply(PlainModel currentData, Happened eventData) => currentData;
      }

      [WhizbangDbContext]
      public class OrdersDbContext : DbContext {
        public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }
      }
    }

    namespace TestApp.Billing {
      [WhizbangDbContext(Schema = "billing")]
      public class BillingDbContext : DbContext {
        public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }
      }
    }
    """;

  private const string SHARED_HINT = "WhizbangMigrationScripts.g.cs";
  private const string FIRST_MIGRATION = "(\"000_MigrationTracking.sql\", global::System.Text.Encoding.UTF8.GetString(";

  /// <summary>Two contexts, one copy of the migration text.</summary>
  /// <remarks>Fails if any context's own file embeds the set again, or if the shared file is emitted per context.</remarks>
  [Test]
  public async Task TheMigrationSetIsEmittedOnceForEveryContextAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(TWO_CONTEXTS);

    var carrying = result.GeneratedSources
      .Where(s => s.SourceText.ToString().Contains(FIRST_MIGRATION, StringComparison.Ordinal))
      .Select(s => s.HintName)
      .ToList();

    await Assert.That(result.GeneratedSources.Count(s => s.HintName.EndsWith("_SchemaExtensions.g.cs", StringComparison.Ordinal)))
      .IsEqualTo(2).Because("both contexts get their schema extensions, or this test says nothing about sharing");
    await Assert.That(carrying).IsEquivalentTo(new[] { SHARED_HINT })
      .Because("the migration text is the same for every context, so it is compiled once, not once per context");
  }

  /// <summary>Each context's initializer reads the shared set, by a name that resolves from its own namespace.</summary>
  [Test]
  public async Task EachContextReadsTheSharedSetAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(TWO_CONTEXTS);

    foreach (var context in new[] { "OrdersDbContext", "BillingDbContext" }) {
      var text = result.GeneratedSources.Single(s => s.HintName == $"{context}_SchemaExtensions.g.cs").SourceText.ToString();
      await Assert.That(text).Contains("global::TestApp.Orders.Generated.WhizbangMigrationScripts.GetMigrationScripts()", StringComparison.Ordinal);
      await Assert.That(text).Contains("global::TestApp.Orders.Generated.WhizbangMigrationScripts.GetBootstrapMigrationScripts()", StringComparison.Ordinal);
      await Assert.That(text).DoesNotContain("u8)", StringComparison.Ordinal)
        .Because($"{context} must carry no migration text of its own");
    }
  }

  /// <summary>The generated sources compile together: the shared class is declared where the contexts look for it.</summary>
  /// <remarks>
  /// The test compilation lacks some of the references a real host has, so this checks only the errors that name the
  /// shared class or sit in its file: a reference to a class that is not there, or one declared twice.
  /// </remarks>
  [Test]
  public async Task TheSharedSetResolvesFromEveryContextAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(TWO_CONTEXTS);
    var parseOptions = (CSharpParseOptions)result.Compilation.SyntaxTrees[0].Options;
    var compilation = result.Compilation.AddSyntaxTrees(result.GeneratedSources.Select(s =>
      CSharpSyntaxTree.ParseText(s.SourceText, parseOptions, path: s.HintName)));

    var errors = compilation.GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error
        && (d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("WhizbangMigrationScripts", StringComparison.Ordinal)
          || d.Location.SourceTree?.FilePath == SHARED_HINT))
      .Select(d => d.ToString())
      .ToList();

    await Assert.That(result.GeneratedSources.Any(s => s.HintName == SHARED_HINT)).IsTrue();
    await Assert.That(errors).IsEmpty();
  }

  /// <summary>An assembly without a Whizbang DbContext gets no migration set.</summary>
  [Test]
  public async Task NoContextEmitsNoSetAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      namespace TestApp;
      public class NotAContext { }
      """);

    await Assert.That(result.GeneratedSources.Any(s => s.HintName == SHARED_HINT)).IsFalse();
  }
}
