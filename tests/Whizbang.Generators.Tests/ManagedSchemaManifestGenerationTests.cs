// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The managed-object manifest the EF Core generator emits: every index the table's DDL builds, the model's
/// <c>[KeepSchemaObject]</c> pins, and the registration that keys the manifest by its context.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
[Category("SourceGenerators")]
public class ManagedSchemaManifestGenerationTests {
  private const string SOURCE = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record LedgerPosted : IEvent;

    [KeepSchemaObject("idx_ledger_legacy", Reason = "the \"monthly\" report reads it")]
    [KeepSchemaObject("idx_ledger_other")]
    public record LedgerModel {
      [StreamId]
      public Guid LedgerId { get; init; }

      [Indexed]
      public string Status { get; init; } = string.Empty;
    }

    public class LedgerPerspective : IPerspectiveFor<LedgerModel, LedgerPosted> {
      public LedgerModel Apply(LedgerModel currentData, LedgerPosted eventData) => currentData;
    }

    [WhizbangDbContext]
    public class LedgerDbContext : DbContext {
      public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }
    }
    """;

  private static async Task<(string Schema, string Registration)> _generateAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(SOURCE);
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal));
    var registration = string.Join("\n", result.GeneratedSources
      .Where(s => s.SourceText.ToString().Contains("ManagedSchemaManifest(", StringComparison.Ordinal)
        && !s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal))
      .Select(s => s.SourceText.ToString()));
    return (schema.SourceText.ToString(), registration);
  }

  [Test]
  public async Task TheManifest_DeclaresTheIndexesTheTablesDdlBuildsAsync() {
    var (schema, _) = await _generateAsync();

    await Assert.That(schema).Contains("objects.Index(\"wh_per_ledger\", \"idx_ledger_status_json\", \"TestApp.LedgerModel\");");
  }

  [Test]
  public async Task EachKeepSchemaObject_IsACodePin_WithItsReasonOrNoneAsync() {
    var (schema, _) = await _generateAsync();

    await Assert.That(schema).Contains(
      "objects.Pin(\"wh_per_ledger\", \"idx_ledger_legacy\", \"the \\\"monthly\\\" report reads it\", \"TestApp.LedgerModel [KeepSchemaObject]\");");
    await Assert.That(schema).Contains(
      "objects.Pin(\"wh_per_ledger\", \"idx_ledger_other\", null, \"TestApp.LedgerModel [KeepSchemaObject]\");");
  }

  [Test]
  public async Task TheRegistration_KeysTheManifestByItsContextAsync() {
    var (_, registration) = await _generateAsync();

    await Assert.That(registration).Contains(
      "services.AddKeyedSingleton<global::Whizbang.Data.Postgres.Schema.ManagedSchemaManifest>(typeof(global::TestApp.LedgerDbContext)");
    await Assert.That(registration).Contains("global::TestApp.Generated.LedgerDbContextSchemaExtensions.GetManagedSchemaObjects");
  }
}
