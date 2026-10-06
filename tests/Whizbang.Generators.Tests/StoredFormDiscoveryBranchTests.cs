// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Stored-form declarations at their edges: a default on a promoted field of a model that is not
/// Split (its document still holds the value, so the default can reach it), a <c>false</c> boolean
/// default, and an empty removed path.
/// </summary>
/// <tests>src/Whizbang.Generators.Shared/Models/StoredFormDiscovery.cs</tests>
[Category("SourceGenerators")]
public class StoredFormDiscoveryBranchTests {
  private const string STEP = "global::Whizbang.Data.Postgres.StoredFormStep.";

  private static async Task<(string Code, List<Diagnostic> Diagnostics)> _runAsync(string modelAttributes, string members, string declarations = "") {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record TicketEvent : IEvent;

      {{modelAttributes}}
      public record TicketModel {
        [StreamId]
        public Guid Id { get; init; }

        {{members}}
      }

      public class TicketPerspective : IPerspectiveFor<TicketModel, TicketEvent> {
        public TicketModel Apply(TicketModel currentData, TicketEvent @event) => currentData;
      }

      {{declarations}}

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var code = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    return (code, [.. result.Diagnostics]);
  }

  /// <summary>
  /// Only a Split model keeps a promoted field out of its document. An Extracted model's document
  /// still carries the field, so a default for it is generated rather than refused.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task DefaultOnAPhysicalFieldOfAnExtractedModel_IsGeneratedAsync() {
    var (code, diagnostics) = await _runAsync(
      "[PerspectiveStorage(FieldStorageMode.Extracted)]",
      "[PhysicalField] [StoredForm(DefaultWhenMissing = 1)] public int Tier { get; init; }");

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ830")).IsEmpty();
    await Assert.That(code).Contains($"{STEP}DefaultWhenMissing(\"Tier\", \"1\")");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task FalseBooleanDefault_IsWrittenAsTheJsonLiteralAsync() {
    var (code, diagnostics) = await _runAsync("", "[StoredForm(DefaultWhenMissing = false)] public bool Archived { get; init; }");

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ830")).IsEmpty();
    await Assert.That(code).Contains($"{STEP}DefaultWhenMissing(\"Archived\", \"false\")");
  }

  /// <summary>
  /// A rename on a promoted field retypes that field's column, found among the model's promoted
  /// fields by name; a vector field ahead of it is passed over rather than matched.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task RenameOnAPhysicalFieldAfterAVectorField_RenamesItsOwnColumnAsync() {
    var (code, diagnostics) = await _runAsync("", """
      [VectorField(3)] public float[]? Embedding { get; init; }
        [PhysicalField] [StoredForm(PreviousName = "Level")] public int Tier { get; init; }
      """);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ830")).IsEmpty();
    await Assert.That(code).Contains($"{STEP}Rename(\"Tier\", \"Level\")");
    await Assert.That(code).Contains($"{STEP}RenameColumn(\"level\", \"tier\")");
    await Assert.That(code).DoesNotContain("embedding\", \"tier");
  }

  /// <summary>
  /// A custom migration the generated code creates through its parameterless constructor needs one
  /// it can call: a private parameterless constructor is reported (WHIZ831) and not registered.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task CustomMigrationWithAPrivateParameterlessConstructor_IsWHIZ831Async() {
    var (code, diagnostics) = await _runAsync("", "", """
      public sealed class PrivateMade : IStoredFormMigration<TicketModel> {
        private PrivateMade() { }
        public string Name => "private-made";
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
      """);

    var reported = diagnostics.Where(d => d.Id == "WHIZ831")
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    await Assert.That(reported).Contains(m => m.Contains("PrivateMade", StringComparison.Ordinal)
      && m.Contains("no public or internal parameterless constructor", StringComparison.Ordinal));
    await Assert.That(code).DoesNotContain("new global::TestApp.PrivateMade()");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EmptyRemovedPath_IsWHIZ830Async() {
    var (code, diagnostics) = await _runAsync("[StoredFormRemoved(\"\")]", "");

    var error = diagnostics.Where(d => d.Id == "WHIZ830").ToList();
    await Assert.That(error).Count().IsEqualTo(1);
    await Assert.That(error[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("not a property key");
    await Assert.That(code).DoesNotContain($"{STEP}Remove(");
  }

  /// <summary>
  /// A removal with no path does not bind (the attribute's constructor takes one), as source an
  /// analyzer sees mid-edit; it is refused the same way as an empty path, not read as removing the model.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task RemovalThatDoesNotBind_IsWHIZ830Async() {
    var (code, diagnostics) = await _runAsync("[StoredFormRemoved]", "");

    var error = diagnostics.Where(d => d.Id == "WHIZ830").ToList();
    await Assert.That(error).Count().IsEqualTo(1);
    await Assert.That(error[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("not a property key");
    await Assert.That(code).DoesNotContain($"{STEP}Remove(");
  }
}
