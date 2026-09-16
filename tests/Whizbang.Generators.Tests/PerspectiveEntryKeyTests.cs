using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The per-perspective hash entries the initializer compares are keyed by table, not by the
/// model's simple type name.
/// </summary>
/// <remarks>
/// A service that nests its models under feature holders has many models named alike. Keyed by
/// simple name, those models shared one hash row, whichever wrote last owned it, and the others
/// read as changed on every start, so the perspective pass re-applied its DDL under the schema
/// lock on every start of every instance. The table name is unique within a schema by
/// construction, so it is the key.
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
public class PerspectiveEntryKeyTests {
  private const string COLLIDING_MODELS = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public static class First {
      public class Model {
        [StreamId]
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
      }
    }

    public static class Second {
      public class Model {
        [StreamId]
        public Guid Id { get; set; }
        public int Count { get; set; }
      }
    }

    public record FirstNoted([property: StreamId] Guid Id) : IEvent;
    public record SecondNoted([property: StreamId] Guid Id) : IEvent;

    public class FirstProjection : IPerspectiveFor<First.Model, FirstNoted> {
      public First.Model Apply(First.Model currentData, FirstNoted eventData) => currentData;
    }

    public class SecondProjection : IPerspectiveFor<Second.Model, SecondNoted> {
      public Second.Model Apply(Second.Model currentData, SecondNoted eventData) => currentData;
    }

    [WhizbangDbContext]
    public class HoldingDbContext : DbContext {
      public HoldingDbContext(DbContextOptions<HoldingDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>Two models with one simple name are two entries, named by their tables.</summary>
  [Test]
  public async Task EntriesAreKeyedByTableAsync() {
    var output = await _generatedAsync(COLLIDING_MODELS);

    await Assert.That(output).Contains("(\"wh_per_first\", @\"", StringComparison.Ordinal)
      .Because("the table is the one name unique to a perspective within its schema");
    await Assert.That(output).Contains("(\"wh_per_second\", @\"", StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("(\"Model\", @\"", StringComparison.Ordinal)
      .Because("keyed by simple name, two models sharing it would share one hash row and never read as current");
  }
}
