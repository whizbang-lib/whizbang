// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// WHIZ605: CI runs a sharded test project as <c>[Category=ShardN]</c> slices, so in a project that declares
/// <c>WhizbangShardedTests</c> every test class must carry exactly one <c>Shard*</c> category, or
/// <c>Benchmark</c> and none. A class with no shard runs in no slice; one with two runs twice.
/// </summary>
public class ShardCategoryAnalyzerTests {
  private const string USINGS = """
    using TUnit.Core;

    """;

  private static readonly ImmutableDictionary<string, string> _sharded =
    ImmutableDictionary<string, string>.Empty.Add("build_property.WhizbangShardedTests", "true");

  private static Task<ImmutableArray<Diagnostic>> _analyzeShardedAsync(string source) =>
    AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), USINGS + source, _sharded);

  [Test]
  public async Task TestClass_WithNoShardCategory_ReportsWhiz605Async() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Integration")]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ605");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'OrdersTests'");
    await Assert.That(message).Contains("[]");
  }

  [Test]
  public async Task TestClass_WithNoCategoryAtAll_ReportsWhiz605Async() {
    var diagnostics = await _analyzeShardedAsync("""
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TestClass_WithTwoShardCategories_ReportsBothShardsAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Shard1")]
      [Category("Shard3")]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("[Shard1, Shard3]");
  }

  [Test]
  public async Task TestClass_WithExactlyOneShard_ReportsNothingAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Integration")]
      [Category("Shard2")]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task TestClass_WithBenchmarkAndNoShard_ReportsNothingAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Benchmark")]
      public class ThroughputTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task TestClass_WithBenchmarkAndAShard_ReportsWhiz605Async() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Benchmark")]
      [Category("Shard4")]
      public class ThroughputTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("[Shard4] and Benchmark");
  }

  [Test]
  public async Task Diagnostic_IsLocatedOnTheClassIdentifierAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    var location = diagnostics.Single().Location;
    var text = (await location.SourceTree!.GetTextAsync()).ToString(location.SourceSpan);
    await Assert.That(text).IsEqualTo("OrdersTests");
  }

  [Test]
  public async Task ShardOnABaseClass_IsCountedForTheDerivedClassAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Shard5")]
      public abstract class PostgresTestBase { }
      public class OrdersTests : PostgresTestBase {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ShardOnABaseClass_PlusOneOnTheDerivedClass_CountsBothAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Shard1")]
      public abstract class PostgresTestBase { }
      [Category("Shard2")]
      public class OrdersTests : PostgresTestBase {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Shard1");
    await Assert.That(message).Contains("Shard2");
  }

  [Test]
  public async Task AbstractBaseWithTests_IsNotReportedButItsConcreteSubclassIsAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public abstract class ContractTests {
        [Test] public void Run() { }
      }
      public class InMemoryContractTests : ContractTests { }
      """);

    var message = diagnostics.Single().GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'InMemoryContractTests'");
  }

  [Test]
  public async Task ClassWithoutTests_ReportsNothingAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public class Fixture {
        public void Helper() { }
      }
      public class Seeder : Fixture {
        [System.Obsolete] public void Seed() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task StructWithATestMethod_ReportsNothingAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public struct NotATestClass {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task CategoryAttributeSubclass_IsCountedAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public sealed class ShardAttribute(string name) : CategoryAttribute(name);
      [Shard("Shard1")]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task CategoryAttributeSubclass_WithoutArguments_CannotBeReadAndIsNotCountedAsync() {
    // The value reaches CategoryAttribute through the subclass's base call, which an attribute's arguments do not
    // show; counting nothing reports the class, so the gap is visible rather than silently passing.
    var diagnostics = await _analyzeShardedAsync("""
      public sealed class ShardOneAttribute : CategoryAttribute {
        public ShardOneAttribute() : base("Shard1") { }
      }
      [ShardOne]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task CategoryFromAConstant_IsReadAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      public static class Shards { public const string ONE = "Shard1"; }
      [Category(Shards.ONE)]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task NestedTestClass_IsCheckedOnItsOwnAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category("Shard1")]
      public class Outer {
        [Test] public void Run() { }
        public class InnerTests {
          [Test] public void Run() { }
        }
      }
      """);

    var message = diagnostics.Single().GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'Outer.InnerTests'");
  }

  [Test]
  public async Task PartialClassAcrossTwoFiles_IsReportedOnceAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), [
      USINGS + """
      public partial class OrdersTests {
        [Test] public void Run() { }
      }
      """,
      USINGS + """
      public partial class OrdersTests {
        [Test] public void RunAgain() { }
      }
      """], _sharded);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PartialClassAcrossTwoFiles_CountsTheShardFromEitherFileAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), [
      USINGS + """
      public partial class OrdersTests {
        [Test] public void Run() { }
      }
      """,
      USINGS + """
      [Category("Shard3")]
      public partial class OrdersTests { }
      """], _sharded);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task CategoryWithANullArgument_IsNotCountedAsync() {
    var diagnostics = await _analyzeShardedAsync("""
      [Category(null)]
      [Category("Shard2")]
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task NonTUnitAttributeNamedCategory_IsIgnoredAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), """
      namespace Elsewhere {
        [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
        public sealed class CategoryAttribute(string name) : System.Attribute {
          public string Name { get; } = name;
        }
      }
      namespace Subject {
        using TUnit.Core;
        [Elsewhere.Category("Shard1")]
        public class OrdersTests {
          [Test] public void Run() { }
        }
      }
      """, _sharded);

    var message = diagnostics.Single().GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("[]");
  }

  [Test]
  public async Task NonTUnitAttributeNamedTest_DoesNotMakeATestClassAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), """
      namespace Elsewhere {
        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class TestAttribute : System.Attribute { }
      }
      namespace Subject {
        public class NotATest {
          [Elsewhere.Test] public void Run() { }
        }
      }
      """, _sharded);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ProjectNotOptedIn_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), USINGS + """
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ProjectOptedOutExplicitly_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), USINGS + """
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """, ImmutableDictionary<string, string>.Empty.Add("build_property.WhizbangShardedTests", "false"));

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task OptInValue_IsCaseInsensitiveAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), USINGS + """
      public class OrdersTests {
        [Test] public void Run() { }
      }
      """, ImmutableDictionary<string, string>.Empty.Add("build_property.WhizbangShardedTests", "True"));

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Compilation_WithoutTUnit_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new ShardCategoryAnalyzer(), """
      public class OrdersTests {
        public void Run() { }
      }
      """, _sharded, referenceTUnit: false);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task Descriptor_CarriesTheRuleAndItsHelpLinkAsync() {
    var descriptor = ShardCategoryAnalyzer.ShardCategoryMismatch;

    await Assert.That(descriptor.Id).IsEqualTo("WHIZ605");
    await Assert.That(descriptor.Category).IsEqualTo("Whizbang.Testing");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
    await Assert.That(descriptor.HelpLinkUri).Contains("test-sharding.md");
    await Assert.That(new ShardCategoryAnalyzer().SupportedDiagnostics).Contains(descriptor);
  }
}
