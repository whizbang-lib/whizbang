// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions.Extensions;
using Whizbang.Data.EFCore.Postgres.Generators;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the embedded migration text is emitted as UTF-8 literals, and that what a consumer decodes
/// at run time is exactly the text the verbatim string literals used to carry.
/// </summary>
/// <remarks>
/// <para>
/// Every ordinary string literal in an assembly shares one user-string heap capped at 16,777,215
/// bytes, and the build fails with CS8103 past it. The whole migration set used to be emitted into
/// that heap, so every migration W! added moved every consumer closer to the cap with nothing to
/// warn before the cliff. UTF-8 literals are compiled into the data section instead, which has no
/// such cap.
/// </para>
/// <para>
/// Asserted on the parsed syntax of the emitted file rather than on its text, so a string literal
/// anywhere in the two lists is found whatever its formatting, and the value compared is the one
/// the compiler would encode.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
public class MigrationUtf8LiteralGenerationTests {
  private const string MINIMAL_CONTEXT = """
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

    [WhizbangDbContext]
    public class PlainDbContext : DbContext {
      public PlainDbContext(DbContextOptions<PlainDbContext> options) : base(options) { }
    }
    """;

  private const string DECODE = "global::System.Text.Encoding.UTF8.GetString";
  private const string FULL_LIST = "GetMigrationScripts";
  private const string BOOTSTRAP_LIST = "GetBootstrapMigrationScripts";

  /// <summary>Both lists carry their SQL as UTF-8 literals, and no ordinary literal but the names.</summary>
  /// <remarks>
  /// Fails if either region is emitted as ordinary string literals again, if one region is moved
  /// and the other forgotten, or if any other ordinary literal of any size creeps into either list.
  /// </remarks>
  [Test]
  [Arguments(FULL_LIST, 100)]
  [Arguments(BOOTSTRAP_LIST, 0)]
  public async Task EveryMigrationIsEmittedAsAUtf8LiteralAsync(string list, int moreThan) {
    var method = _method(await _schemaExtensionsRootAsync(), list);
    var entries = _entries(method);

    await Assert.That(entries.Count).IsGreaterThan(moreThan);
    foreach (var (name, sql) in entries) {
      await Assert.That(_utf8Literal(sql)).IsNotNull()
        .Because($"{name} has to be decoded from a UTF-8 literal, or it counts toward the user-string heap; "
          + $"emitted as: {_head(sql)}");
    }

    var ordinaryLiterals = method.DescendantNodes()
      .Count(n => n.IsKind(SyntaxKind.StringLiteralExpression));
    await Assert.That(ordinaryLiterals).IsEqualTo(entries.Count)
      .Because("the file names are the only ordinary string literals the lists may carry");
  }

  /// <summary>The full list decodes, file by file and in order, to the text it carried before.</summary>
  /// <remarks>
  /// The expected text is derived here from the generator's own embedded resources by the escaping
  /// the verbatim literals applied: the schema token preserved as <c>__MIGRATION_SCHEMA__</c> for
  /// the runtime transform, and braces doubled for <c>ExecuteSqlRawAsync</c>. Quote doubling is
  /// literal syntax, not value, so it is not part of the expected text. Fails if a file is dropped,
  /// reordered or renamed, if either escape is lost or applied twice, if quotes reach the value
  /// doubled, or if a template-wide replacement reaches into the SQL.
  /// </remarks>
  [Test]
  public async Task TheFullListDecodesToTheTextEmittedBeforeAsync() {
    var expected = _embeddedMigrations()
      .Select(m => (m.Name, Sql: _escapedAsBefore(m.Sql)))
      .ToList();

    await _assertDecodesToAsync(FULL_LIST, expected);
  }

  /// <summary>The bootstrap list decodes to the marked regions, escaped exactly as before.</summary>
  /// <remarks>Fails for the same reasons as the full list, applied to the bootstrap regions.</remarks>
  [Test]
  public async Task TheBootstrapListDecodesToTheTextEmittedBeforeAsync() {
    var expected = _embeddedMigrations()
      .Select(m => (m.Name, Sql: MigrationBootstrapRegions.Extract(m.Sql)))
      .Where(m => m.Sql is not null)
      .Select(m => (m.Name, Sql: _escapedAsBefore(m.Sql!)))
      .ToList();

    await _assertDecodesToAsync(BOOTSTRAP_LIST, expected);
  }

  /// <summary>The decoded lists are cached, so the text is decoded once rather than on every call.</summary>
  /// <remarks>
  /// The initializer asks for the full list several times on one start, and each decode of the
  /// whole set would allocate megabytes the string literals never did. Fails if either method
  /// builds its array on every call.
  /// </remarks>
  [Test]
  [Arguments(FULL_LIST)]
  [Arguments(BOOTSTRAP_LIST)]
  public async Task EachListIsDecodedOnceAsync(string list) {
    var method = _method(await _schemaExtensionsRootAsync(), list);

    var coalescing = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
      .Where(a => a.IsKind(SyntaxKind.CoalesceAssignmentExpression))
      .ToList();
    await Assert.That(coalescing.Count).IsEqualTo(1)
      .Because("the list is assigned to its cache only when the cache is empty");
    await Assert.That(coalescing[0].Right.DescendantNodes().OfType<TupleExpressionSyntax>().Any()).IsTrue()
      .Because("the entries themselves are what is built once");
  }

  private static async Task _assertDecodesToAsync(string list, List<(string Name, string Sql)> expected) {
    // Each escape has to occur somewhere in the set, or comparing against it would prove nothing.
    await Assert.That(expected.Any(m => m.Sql.Contains("__MIGRATION_SCHEMA__", StringComparison.Ordinal))).IsTrue();
    await Assert.That(expected.Any(m => m.Sql.Contains("{{", StringComparison.Ordinal))).IsTrue();
    await Assert.That(expected.Any(m => m.Sql.Contains('"', StringComparison.Ordinal))).IsTrue();

    var entries = _entries(_method(await _schemaExtensionsRootAsync(), list));

    await Assert.That(entries.Select(e => e.Name).ToList()).IsEquivalentTo(expected.Select(e => e.Name).ToList())
      .Because("every file is emitted, once, under its own name");
    for (var i = 0; i < expected.Count; i++) {
      await Assert.That(entries[i].Name).IsEqualTo(expected[i].Name)
        .Because("the files are applied in the order they are emitted");
      var literal = _utf8Literal(entries[i].Sql);
      await Assert.That(literal).IsNotNull()
        .Because($"{entries[i].Name} has to be emitted as a UTF-8 literal; emitted as: {_head(entries[i].Sql)}");
      // What the compiler stores (the literal's value encoded as UTF-8) and what the consumer reads
      // back (those bytes decoded) at run time.
      var decoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(literal!.Token.ValueText));
      await Assert.That(decoded == expected[i].Sql).IsTrue()
        .Because($"{entries[i].Name} has to decode to exactly the text it carried as a verbatim literal");
    }
  }

  private static string _escapedAsBefore(string sql) => sql
    .Replace("__SCHEMA__", "__MIGRATION_SCHEMA__")
    .Replace("{", "{{")
    .Replace("}", "}}");

  private static List<(string Name, string Sql)> _embeddedMigrations() {
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;
    var prefix = $"{assembly.GetName().Name}.Templates.Migrations.";
    var migrations = new List<(string Name, string Sql)>();
    foreach (var resource in assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)) {
      using var stream = assembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      migrations.Add((resource[prefix.Length..], reader.ReadToEnd()));
    }

    return migrations;
  }

  private static async Task<SyntaxNode> _schemaExtensionsRootAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MINIMAL_CONTEXT);
    var source = result.GeneratedSources
      .Select(s => s.SourceText.ToString())
      .Single(s => s.Contains($"{FULL_LIST}() {{", StringComparison.Ordinal));
    return await CSharpSyntaxTree.ParseText(source).GetRootAsync();
  }

  private static MethodDeclarationSyntax _method(SyntaxNode root, string name) =>
    root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name);

  // The (name, sql) tuples of one list: the name is always an ordinary string literal.
  private static List<(string Name, ExpressionSyntax Sql)> _entries(MethodDeclarationSyntax method) =>
    [.. method.DescendantNodes().OfType<TupleExpressionSyntax>()
      .Where(t => t.Arguments.Count == 2 && t.Arguments[0].Expression.IsKind(SyntaxKind.StringLiteralExpression))
      .Select(t => (((LiteralExpressionSyntax)t.Arguments[0].Expression).Token.ValueText, t.Arguments[1].Expression))];

  // The UTF-8 literal inside the decode call, or null when the SQL is emitted any other way.
  private static LiteralExpressionSyntax? _utf8Literal(ExpressionSyntax sql) =>
    sql is InvocationExpressionSyntax { ArgumentList.Arguments: [var argument] } invocation
      && invocation.Expression.ToString() == DECODE
      && argument.Expression is LiteralExpressionSyntax literal
      && literal.IsKind(SyntaxKind.Utf8StringLiteralExpression)
        ? literal
        : null;

  private static string _head(ExpressionSyntax sql) {
    var text = sql.ToString();
    return text.Length > 60 ? text[..60] : text;
  }
}
