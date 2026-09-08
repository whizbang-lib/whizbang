using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A <c>DbContext</c> declared in the global namespace with no explicit <c>Schema</c> derives the
/// default Postgres schema (issue #707). Both EF generators guarded the derivation against an empty
/// namespace string, but Roslyn renders the global namespace as the literal text
/// <c>&lt;global namespace&gt;</c>, which is never empty, so the DDL came out as
/// <c>CREATE SCHEMA IF NOT EXISTS "&lt;global namespace&gt;"</c>. The guard must ask the symbol
/// whether it is the global namespace, not inspect the display string.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</code-under-test>
/// <docs>fundamentals/perspectives/efcore-perspectives</docs>
public class EFCoreGeneratorsGlobalNamespaceSchemaTests {
  private const string GLOBAL_NAMESPACE_SOURCE = """
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Data.EFCore.Custom;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    public record GlobalEvent : IEvent;

    public record GlobalModel {
      public string Id { get; init; } = "";
    }

    public class GlobalPerspective : IPerspectiveFor<GlobalModel, GlobalEvent> {
      public GlobalModel Apply(GlobalModel currentData, GlobalEvent eventData) => currentData;
    }

    [WhizbangDbContext]
    public class GlobalDbContext : DbContext {
      public GlobalDbContext(DbContextOptions<GlobalDbContext> options) : base(options) { }
    }
    """;

  [Test]
  public async Task ServiceRegistrationGenerator_DbContextInTheGlobalNamespace_DerivesThePublicSchemaAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(GLOBAL_NAMESPACE_SOURCE);

    var generated = result.GeneratedSources.Select(s => s.SourceText.ToString()).ToList();
    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated.Any(s => s.Contains("<global namespace>", StringComparison.Ordinal))).IsFalse()
      .Because("Roslyn's placeholder for the global namespace is display text, never a schema name");
    await Assert.That(generated.Any(s => s.Contains("\"public\"", StringComparison.Ordinal))).IsTrue()
      .Because("with no namespace to derive from, the default Postgres schema is the schema");
  }

  [Test]
  public async Task PerspectiveConfigurationGenerator_DbContextInTheGlobalNamespace_DerivesThePublicSchemaAsync() {
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(GLOBAL_NAMESPACE_SOURCE);

    var generated = result.GeneratedSources.Select(s => s.SourceText.ToString()).ToList();
    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated.Any(s => s.Contains("<global namespace>", StringComparison.Ordinal))).IsFalse()
      .Because("Roslyn's placeholder for the global namespace is display text, never a schema name");
    await Assert.That(generated.Any(s => s.Contains("\"public\"", StringComparison.Ordinal))).IsTrue()
      .Because("with no namespace to derive from, the default Postgres schema is the schema");
  }
}
