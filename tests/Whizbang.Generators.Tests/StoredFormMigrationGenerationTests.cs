using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What the schema generator emits for an app's stored-form declarations: one journaled migration per declaration,
/// built from <c>StoredFormStep</c> calls the runtime turns into SQL, in the order renames, conversions, defaults,
/// removals, then the app's custom migrations; a physical field's column is retyped or renamed with its document;
/// and a declaration it cannot generate is a build error rather than a silent no-op. The emitted lines are pinned
/// exactly, as snapshots of the generated schema extension.
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/StoredFormDiscovery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
public class StoredFormMigrationGenerationTests {
  private const string STEP = "global::Whizbang.Data.Postgres.StoredFormStep.";
  private const string NUMBER = "global::Whizbang.Data.Postgres.StoredNumber.";

  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public enum Stage { Draft, Open, Closed }
    [Flags] public enum Access { None = 0, Read = 1, Write = 2 }

    public record OrderEvent : IEvent;

    public record Address {
      [StoredForm(PreviousName = "Street")]
      public string Line1 { get; init; } = "";
    }

    public record OrderLine {
      [StoredForm(Previously = typeof(int))]
      public string Sku { get; init; } = "";
    }

    [StoredFormRemoved("Legacy")]
    [StoredFormRemoved("Shipping.Note")]
    public record OrderModel {
      [StreamId]
      public Guid Id { get; init; }

      [StoredForm(Previously = typeof(int))]
      public string Status { get; init; } = "";

      [StoredForm(Previously = typeof(string))]
      public int Quantity { get; init; }

      [StoredForm(Previously = typeof(string))]
      public Stage? Stage { get; init; }

      [StoredForm(Previously = typeof(string))]
      public Access Access { get; init; }

      [StoredForm(Previously = typeof(Stage))]
      public string Phase { get; init; } = "";

      [StoredForm(PreviousName = "Name", Previously = typeof(int))]
      public string DisplayName { get; init; } = "";

      [StoredForm(DefaultWhenMissing = 1)]
      public int Tier { get; init; }

      [StoredForm(DefaultWhenMissing = "n/a \"quoted\"")]
      public string Note { get; init; } = "";

      [StoredForm(DefaultWhenMissing = true)]
      public bool Active { get; init; }

      [StoredForm(DefaultWhenMissing = TestApp.Stage.Open)]
      public Stage Initial { get; init; }

      [StoredForm(DefaultWhenMissing = 2.5)]
      public decimal Rate { get; init; }

      public Address Shipping { get; init; } = new();

      public System.Collections.Generic.List<OrderLine> Lines { get; init; } = [];
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
      public OrderModel Apply(OrderModel currentData, OrderEvent @event) => currentData;
    }

    public sealed class SplitFullName : IStoredFormMigration<OrderModel> {
      public string Name => "2026-10-split";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public sealed class AnotherOne : IStoredFormMigration<OrderModel> {
      public string Name => "2026-09-another";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public abstract class NotConstructible : IStoredFormMigration<OrderModel> {
      public string Name => "never";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public record UnusedModel(string Name);

    public sealed class Orphan : IStoredFormMigration<UnusedModel> {
      public string Name => "orphan";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<(string Code, IReadOnlyList<Diagnostic> Diagnostics)> _runAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var code = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    return (code, result.Diagnostics.ToList());
  }

  private static string _migration(string name, params string[] steps) =>
    $"global::Whizbang.Data.Postgres.StoredFormMigrationSql.Generated(\"testapp\", \"wh_per_order\", \"wh_per_order.{name}\", {string.Join(", ", steps)}, {_retry("TestApp.OrderPerspective")}),";

  private static string _retry(params string[] perspectives) =>
    $"{STEP}RetryParkedStreams({string.Join(", ", perspectives.Select(p => $"\"{p}\""))})";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheSampleModel_CompilesSoEveryAttributeBindsAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var errors = string.Join("\n", result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
    await Assert.That(errors).IsEqualTo("")
      .Because("An attribute that does not bind has no arguments, and the tests below would pass or fail for the wrong reason.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EachTypeChange_BecomesAStepFromTheFormerTypeToTheCurrentAsync() {
    var (code, _) = await _runAsync(MODEL);

    await Assert.That(code).Contains(_migration("Status:Int32->String", $"{STEP}ToText(\"Status\")"));
    await Assert.That(code).Contains(_migration("Quantity:String->Int32", $"{STEP}ToNumber(\"Quantity\", {NUMBER}Int32)"));
    await Assert.That(code).Contains(_migration("Stage:String->Stage",
      $"{STEP}ToEnumNumber(\"Stage\", \"Stage\", {NUMBER}Int32, new (string Name, string Value)[] {{ (\"Draft\", \"0\"), (\"Open\", \"1\"), (\"Closed\", \"2\") }}, false)"))
      .Because("A nullable enum converts like its underlying enum, from the enum's own members.");
    await Assert.That(code).Contains(_migration("Access:String->Access",
      $"{STEP}ToEnumNumber(\"Access\", \"Access\", {NUMBER}Int32, new (string Name, string Value)[] {{ (\"None\", \"0\"), (\"Read\", \"1\"), (\"Write\", \"2\") }}, true)"));
    await Assert.That(code).Contains(_migration("Phase:Stage->String",
      $"{STEP}EnumNumberToName(\"Phase\", new (string Name, string Value)[] {{ (\"Draft\", \"0\"), (\"Open\", \"1\"), (\"Closed\", \"2\") }})"));
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RenamesDefaultsAndRemovals_AreGenerated_NestedPathsTooAsync() {
    var (code, _) = await _runAsync(MODEL);

    await Assert.That(code).Contains(_migration("DisplayName:renamed-from:Name", $"{STEP}Rename(\"DisplayName\", \"Name\")"));
    await Assert.That(code).Contains(_migration("DisplayName:Int32->String", $"{STEP}ToText(\"DisplayName\")"));
    await Assert.That(code).Contains(_migration("Shipping.Line1:renamed-from:Street", $"{STEP}Rename(\"Shipping.Line1\", \"Street\")"));
    await Assert.That(code).Contains(_migration("Tier:default", $"{STEP}DefaultWhenMissing(\"Tier\", \"1\")"));
    await Assert.That(code).Contains(_migration("Note:default", $"{STEP}DefaultWhenMissing(\"Note\", \"\\\"n/a \\\\\\\"quoted\\\\\\\"\\\"\")"))
      .Because("A string default is written as a JSON string, escaped for JSON and then for C#.");
    await Assert.That(code).Contains(_migration("Active:default", $"{STEP}DefaultWhenMissing(\"Active\", \"true\")"));
    await Assert.That(code).Contains(_migration("Initial:default", $"{STEP}DefaultWhenMissing(\"Initial\", \"1\")"))
      .Because("An enum default is stored as its number.");
    await Assert.That(code).Contains(_migration("Rate:default", $"{STEP}DefaultWhenMissing(\"Rate\", \"2.5\")"));
    await Assert.That(code).Contains(_migration("Legacy:removed", $"{STEP}Remove(\"Legacy\")"));
    await Assert.That(code).Contains(_migration("Shipping.Note:removed", $"{STEP}Remove(\"Shipping.Note\")"));
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Migrations_RunRenamesThenConversionsThenDefaultsThenRemovals_ThenCustomByNameWithoutAnOrderAsync() {
    var (code, diagnostics) = await _runAsync(MODEL);

    int at(string text) => code.IndexOf(text, StringComparison.Ordinal);
    await Assert.That(at("DisplayName:renamed-from:Name")).IsLessThan(at("Status:Int32->String"));
    await Assert.That(at("Shipping.Line1:renamed-from:Street")).IsLessThan(at("Status:Int32->String"));
    await Assert.That(at("DisplayName:Int32->String")).IsLessThan(at("Tier:default"));
    await Assert.That(at("Rate:default")).IsLessThan(at("Legacy:removed"));
    await Assert.That(at("Shipping.Note:removed")).IsLessThan(at("StoredFormMigrationSql.Custom("));
    await Assert.That(code).Contains(
      "global::Whizbang.Data.Postgres.StoredFormMigrationSql.Custom(\"testapp\", \"wh_per_order\", new global::TestApp.AnotherOne(), \"TestApp.OrderPerspective\"),")
      .Because("A custom migration brings forward the retries of the table's parked streams too.");
    await Assert.That(at("new global::TestApp.AnotherOne()")).IsLessThan(at("new global::TestApp.SplitFullName()"))
      .Because("Migrations that state no Order compile against the default and run in type-name order, as before.");
    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ831" or "WHIZ833").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
      .DoesNotContain(m => m.Contains("SplitFullName", StringComparison.Ordinal) || m.Contains("AnotherOne", StringComparison.Ordinal))
      .Because("Two migrations that both take the default Order are not a clash anyone declared.");
    await Assert.That(code).DoesNotContain("NotConstructible").Because("An abstract class cannot be instantiated.");
    await Assert.That(code).DoesNotContain("TestApp.Orphan");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheMigrations_RunInTheRewritePhaseBeforeTheTemporalRewrite_AndStatusIsExposedAsync() {
    var (code, _) = await _runAsync(MODEL);

    await Assert.That(code).Contains("internal static global::Whizbang.Data.Postgres.StoredFormMigration[] GetStoredFormMigrations()");
    await Assert.That(code).Contains(
      "global::Whizbang.Data.Postgres.StoredFormMigrationSql.DeclareAsync(\n          rewriteConnectionFactory, \"testapp\", storedFormMigrations, cancellationToken);")
      .Because("Every declared migration is recorded as pending before any runs, outside the phase's transaction.");
    await Assert.That(code).Contains("global::Whizbang.Data.Postgres.StoredFormMigrationSql.ForPhase(\"testapp\", storedFormMigrations)");
    await Assert.That(code.IndexOf("StoredFormMigrationSql.ForPhase(", StringComparison.Ordinal))
      .IsLessThan(code.IndexOf("CanonicalTemporalRewrite.ForModel(", StringComparison.Ordinal))
      .Because("A value a rename moves is then converted to the canonical temporal form in the same pass.");
    await Assert.That(code).Contains("public static async Task<System.Collections.Generic.IReadOnlyList<global::Whizbang.Data.Postgres.StoredFormMigrationStatus>> GetStoredFormMigrationStatusAsync(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADeclarationInsideACollectionElement_IsWHIZ832_AndAnOrphanMigrationWHIZ831Async() {
    var (code, diagnostics) = await _runAsync(MODEL);

    var inCollection = diagnostics.Where(d => d.Id == "WHIZ832").ToList();
    await Assert.That(inCollection).Count().IsEqualTo(1);
    await Assert.That(inCollection[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(inCollection[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Lines");
    await Assert.That(code).DoesNotContain("Sku");

    var orphan = diagnostics.Where(d => d.Id == "WHIZ831").ToList();
    await Assert.That(orphan).Count().IsEqualTo(1);
    await Assert.That(orphan[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("TestApp.Orphan");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ830")).IsEmpty();
  }

  private const string PHYSICAL = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public enum Stage { Draft, Open }

    public record ItemEvent : IEvent;

    public record ItemModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      [StoredForm(Previously = typeof(int))]
      public string Code { get; init; } = "";

      [PhysicalField]
      [StoredForm(Previously = typeof(string))]
      public long Count { get; init; }

      [PhysicalField]
      [StoredForm(Previously = typeof(Stage))]
      public string Phase { get; init; } = "";

      [PhysicalField]
      [StoredForm(Previously = typeof(string))]
      public Stage Stage { get; init; }

      [PhysicalField]
      [StoredForm(PreviousName = "Label")]
      public string Title { get; init; } = "";

      [PhysicalField(ColumnName = "fixed_col")]
      [StoredForm(PreviousName = "Old")]
      public string Kept { get; init; } = "";
    }

    public class ItemPerspective : IPerspectiveFor<ItemModel, ItemEvent> {
      public ItemModel Apply(ItemModel currentData, ItemEvent @event) => currentData;
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record SplitModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      [StoredForm(Previously = typeof(int))]
      public string Code { get; init; } = "";
    }

    public class SplitPerspective : IPerspectiveFor<SplitModel, ItemEvent> {
      public SplitModel Apply(SplitModel currentData, ItemEvent @event) => currentData;
    }

    [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
    public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
      public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APhysicalField_RetypesOrRenamesItsColumnWithItsDocumentAsync() {
    var (code, _) = await _runAsync(PHYSICAL);

    string item(string name, params string[] steps) =>
      $"global::Whizbang.Data.Postgres.StoredFormMigrationSql.Generated(\"testapp\", \"wh_per_item\", \"wh_per_item.{name}\", {string.Join(", ", steps)}, {_retry("TestApp.ItemPerspective")}),";
    await Assert.That(code).Contains(item("Code:Int32->String",
      $"{STEP}ToText(\"Code\")", $"{STEP}RetypeColumn(\"code\", \"TEXT\", null, null)"));
    await Assert.That(code).Contains(item("Count:String->Int64",
      $"{STEP}ToNumber(\"Count\", {NUMBER}Int64)", $"{STEP}RetypeColumn(\"count\", \"BIGINT\", {NUMBER}Int64, null)"));
    await Assert.That(code).Contains(item("Phase:Stage->String",
      $"{STEP}EnumNumberToName(\"Phase\", new (string Name, string Value)[] {{ (\"Draft\", \"0\"), (\"Open\", \"1\") }})",
      $"{STEP}RetypeColumn(\"phase\", \"TEXT\", null, new (string Name, string Value)[] {{ (\"Draft\", \"0\"), (\"Open\", \"1\") }})"))
      .Because("A former enum column holds numbers; retyped to text it takes the member names, as the document does.");
    await Assert.That(code).Contains(item("Stage:String->Stage",
      $"{STEP}ToEnumNumber(\"Stage\", \"Stage\", {NUMBER}Int32, new (string Name, string Value)[] {{ (\"Draft\", \"0\"), (\"Open\", \"1\") }}, false)"))
      .Because("A text enum column is left to the enum column rewrite, which already converts names to numbers.");
    await Assert.That(code).DoesNotContain("RetypeColumn(\"stage\"");
    await Assert.That(code).Contains(item("Title:renamed-from:Label",
      $"{STEP}Rename(\"Title\", \"Label\")", $"{STEP}RenameColumn(\"label\", \"title\")"));
    await Assert.That(code).Contains(item("Kept:renamed-from:Old", $"{STEP}Rename(\"Kept\", \"Old\")"))
      .Because("A declared column name did not change with the property, so the column is left alone.");

    await Assert.That(code).Contains(
      "global::Whizbang.Data.Postgres.StoredFormMigrationSql.Generated(\"testapp\", \"wh_per_split\", \"wh_per_split.Code:Int32->String\", "
      + $"{STEP}RetypeColumn(\"code\", \"TEXT\", null, null), {_retry("TestApp.SplitPerspective")}),")
      .Because("A Split model keeps the field only in its column, so there is no document path to convert.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("[StoredForm(Previously = typeof(Guid))] public int Bad { get; init; }", "Guid")]
  [Arguments("[StoredForm(Previously = typeof(int))] public int Bad { get; init; }", "current type")]
  [Arguments("[StoredForm(Previously = typeof(string))] public Wide Bad { get; init; }", "ulong")]
  [Arguments("[StoredForm(DefaultWhenMissing = \"x\")] public int Bad { get; init; }", "default")]
  [Arguments("[StoredForm(DefaultWhenMissing = typeof(int))] public int Bad { get; init; }", "default")]
  [Arguments("[StoredForm(DefaultWhenMissing = 1)] public Nested Bad { get; init; } = new();", "default")]
  [Arguments("[StoredForm(Previously = typeof(bool))] public int Bad { get; init; }", "Boolean")]
  [Arguments("[StoredForm(Previously = typeof(Nested))] public string Bad { get; init; } = \"\";", "Nested")]
  [Arguments("[StoredForm(DefaultWhenMissing = 1)] public string Bad { get; init; } = \"\";", "default")]
  public async Task ADeclarationItCannotGenerate_IsWHIZ830_AndEmitsNothingAsync(string property, string mentioned) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [Flags] public enum Wide : ulong { A = 1, B = 2 }
      public record Nested { public int X { get; init; } }
      public record BadEvent : IEvent;

      public record BadModel {
        [StreamId]
        public Guid Id { get; init; }

        {{property}}
      }

      public class BadPerspective : IPerspectiveFor<BadModel, BadEvent> {
        public BadModel Apply(BadModel currentData, BadEvent @event) => currentData;
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(source);

    var error = diagnostics.Where(d => d.Id == "WHIZ830").ToList();
    await Assert.That(error).Count().IsEqualTo(1);
    await Assert.That(error[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = error[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("TestApp.BadModel.Bad");
    await Assert.That(message).Contains(mentioned);
    await Assert.That(code).DoesNotContain("wh_per_bad.Bad");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADefaultOnASplitPhysicalField_IsWHIZ830Async() {
    const string SOURCE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record SEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record SModel {
        [StreamId]
        public Guid Id { get; init; }

        [PhysicalField]
        [StoredForm(DefaultWhenMissing = 1)]
        public int Tier { get; init; }
      }

      public class SPerspective : IPerspectiveFor<SModel, SEvent> {
        public SModel Apply(SModel currentData, SEvent @event) => currentData;
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(SOURCE);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ830")).IsEqualTo(1);
    await Assert.That(code).DoesNotContain("Tier:default");
  }

  private const string EDGES = """
    using System;
    using System.Collections.Generic;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public enum Empty { }
    public enum Other { A }
    public enum Kind { A, B }

    public record EEvent : IEvent;

    [StoredFormRemoved("X")]
    public record Entry { public int X { get; init; } }

    public struct Point {
      [StoredForm(PreviousName = "Lat")]
      public double Y { get; init; }
    }

    public record Node {
      public Node? Next { get; init; }
      [StoredForm(PreviousName = "Old")]
      public string Label { get; init; } = "";
    }

    [StoredFormRemoved("Bad Path")]
    public record EdgeModel {
      [StreamId]
      public Guid Id { get; init; }

      [StoredForm(PreviousName = "not a key")]
      public string A { get; init; } = "";

      [StoredForm(DefaultWhenMissing = null)]
      public string B { get; init; } = "";

      [StoredForm(DefaultWhenMissing = 'x')]
      public string C { get; init; } = "";

      [StoredForm(DefaultWhenMissing = "a\\b\u0001")]
      public string D { get; init; } = "";

      [StoredForm(DefaultWhenMissing = 2.5)]
      public int E { get; init; }

      [StoredForm(DefaultWhenMissing = double.NaN)]
      public double F { get; init; }

      [StoredForm(DefaultWhenMissing = 1.5f)]
      public float G { get; init; }

      [StoredForm(DefaultWhenMissing = TestApp.Other.A)]
      public Kind H { get; init; }

      [StoredForm(DefaultWhenMissing = true)]
      public int I { get; init; }

      [StoredForm(Previously = typeof(string))]
      public Empty J { get; init; }

      [StoredForm(Previously = typeof(bool))]
      public string K { get; init; } = "";

      public Dictionary<string, Entry> Entries { get; init; } = [];

      public Point? Where { get; init; }

      public Node Root { get; init; } = new();
    }

    public class EdgePerspective : IPerspectiveFor<EdgeModel, EEvent> {
      public EdgeModel Apply(EdgeModel currentData, EEvent @event) => currentData;
    }

    public sealed class Generic<T> : IStoredFormMigration<EdgeModel> {
      public string Name => "generic";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public sealed class NeedsArgs(int x) : IStoredFormMigration<EdgeModel> {
      public string Name => "args" + x;
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public static class Holder {
      private sealed class Hidden : IStoredFormMigration<EdgeModel> {
        public string Name => "hidden";
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
    }

    [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
    public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
      public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EdgeDeclarations_AreGeneratedOrReported_AndMigrationsThatCannotBeCreatedAreWHIZ831Async() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(EDGES);
    await Assert.That(string.Join("\n", result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)))
      .IsEqualTo("");
    var (code, diagnostics) = await _runAsync(EDGES);
    string edge(string name, string step) =>
      $"global::Whizbang.Data.Postgres.StoredFormMigrationSql.Generated(\"testapp\", \"wh_per_edge\", \"wh_per_edge.{name}\", {step}, {_retry("TestApp.EdgePerspective")}),";
    string messages(string id) => string.Join("\n", diagnostics.Where(d => d.Id == id).Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));

    await Assert.That(code).Contains(edge("C:default", $"{STEP}DefaultWhenMissing(\"C\", \"\\\"x\\\"\")"))
      .Because("A char default is a one-character string.");
    await Assert.That(code).Contains(edge("D:default", $"{STEP}DefaultWhenMissing(\"D\", \"\\\"a\\\\\\\\b\\\\u0001\\\"\")"))
      .Because("A backslash and a control character are escaped for JSON, then for C#.");
    await Assert.That(code).Contains(edge("G:default", $"{STEP}DefaultWhenMissing(\"G\", \"1.5\")"));
    await Assert.That(code).Contains(edge("J:String->Empty",
      $"{STEP}ToEnumNumber(\"J\", \"Empty\", {NUMBER}Int32, new (string Name, string Value)[] {{ }}, false)"));
    await Assert.That(code).Contains(edge("Where.Y:renamed-from:Lat", $"{STEP}Rename(\"Where.Y\", \"Lat\")"))
      .Because("A nullable struct is walked like the struct.");
    await Assert.That(code).Contains(edge("Root.Label:renamed-from:Old", $"{STEP}Rename(\"Root.Label\", \"Old\")"));
    await Assert.That(code).DoesNotContain("Root.Next.Label").Because("A type already on the path is not walked again.");
    await Assert.That(code).DoesNotContain("wh_per_edge.B:").Because("A null default declares nothing.");

    var cannot = messages("WHIZ830");
    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ830")).IsEqualTo(6);
    await Assert.That(cannot).Contains("TestApp.EdgeModel.Bad Path");
    await Assert.That(cannot).Contains("PreviousName 'not a key'");
    await Assert.That(cannot).Contains("TestApp.EdgeModel.E");
    await Assert.That(cannot).Contains("TestApp.EdgeModel.F");
    await Assert.That(cannot).Contains("TestApp.EdgeModel.H");
    await Assert.That(cannot).Contains("TestApp.EdgeModel.I");
    await Assert.That(code).Contains(edge("K:Boolean->String", $"{STEP}ToText(\"K\")"));

    await Assert.That(messages("WHIZ832")).Contains("TestApp.EdgeModel.Entries[].X")
      .Because("A dictionary's values are collection elements too.");

    var never = messages("WHIZ831");
    await Assert.That(never).Contains("generic class");
    await Assert.That(never).Contains("parameterless constructor");
    await Assert.That(never).Contains("not visible");
    await Assert.That(code).DoesNotContain("StoredFormMigrationSql.Custom(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AModelWithoutDeclarations_EmitsAnEmptyListAsync() {
    const string SOURCE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record PEvent : IEvent;
      public record PlainModel { [StreamId] public Guid Id { get; init; } public string Name { get; init; } = ""; }
      public class PlainPerspective : IPerspectiveFor<PlainModel, PEvent> {
        public PlainModel Apply(PlainModel currentData, PEvent @event) => currentData;
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(SOURCE);

    await Assert.That(code).DoesNotContain("StoredFormMigrationSql.Generated(");
    await Assert.That(code).Contains("return new global::Whizbang.Data.Postgres.StoredFormMigration[] {");
    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ830" or "WHIZ831" or "WHIZ832")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AConversionToANarrowOrUnsignedNumber_NamesThatWidthAsync() {
    const string SOURCE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record WEvent : IEvent;
      public record WidthModel {
        [StreamId] public Guid Id { get; init; }
        [StoredForm(Previously = typeof(string))] public sbyte Tiny { get; init; }
        [StoredForm(Previously = typeof(string))] public byte Octet { get; init; }
        [StoredForm(Previously = typeof(string))] public short Small { get; init; }
        [StoredForm(Previously = typeof(string))] public ushort Port { get; init; }
        [StoredForm(Previously = typeof(string))] public uint Count { get; init; }
      }
      public class WidthPerspective : IPerspectiveFor<WidthModel, WEvent> {
        public WidthModel Apply(WidthModel currentData, WEvent @event) => currentData;
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(SOURCE);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ830" or "WHIZ831" or "WHIZ832")).IsEmpty();
    await Assert.That(code).Contains($"{STEP}ToNumber(\"Tiny\", {NUMBER}SByte)");
    await Assert.That(code).Contains($"{STEP}ToNumber(\"Octet\", {NUMBER}Byte)");
    await Assert.That(code).Contains($"{STEP}ToNumber(\"Small\", {NUMBER}Int16)");
    await Assert.That(code).Contains($"{STEP}ToNumber(\"Port\", {NUMBER}UInt16)");
    await Assert.That(code).Contains($"{STEP}ToNumber(\"Count\", {NUMBER}UInt32)")
      .Because("Each width bounds the conversion differently, so the step names the property's own width.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PreviouslyNull_DeclaresNoConversion_AndIsNotAnErrorAsync() {
    const string SOURCE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record NEvent : IEvent;
      public record NullModel {
        [StreamId] public Guid Id { get; init; }
        [StoredForm(Previously = null)] public string Label { get; init; } = "";
      }
      public class NullPerspective : IPerspectiveFor<NullModel, NEvent> {
        public NullModel Apply(NullModel currentData, NEvent @event) => currentData;
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(SOURCE);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ830" or "WHIZ831" or "WHIZ832")).IsEmpty();
    await Assert.That(code).DoesNotContain("StoredFormMigrationSql.Generated(")
      .Because("An unset former type is no type change: there is nothing to convert.");
  }

  private const string ORDERED = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public record OEvent : IEvent;
    public record OModel {
      [StreamId] public Guid Id { get; init; }
    }
    public record PModel {
      [StreamId] public Guid Id { get; init; }
    }
    public class OPerspective : IPerspectiveFor<OModel, OEvent> {
      public OModel Apply(OModel currentData, OEvent @event) => currentData;
    }
    public class PPerspective : IPerspectiveFor<PModel, OEvent> {
      public PModel Apply(PModel currentData, OEvent @event) => currentData;
    }

    public static class Orders {
      public const int LATE = 30;
      public const byte SMALL = 5;
    }

    // Declared in the order the class names sort, which is not the order they run in.
    public sealed class A_Last : IStoredFormMigration<OModel> {
      public string Name => "a";
      public int Order => Orders.LATE;
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }
    public sealed class B_First : IStoredFormMigration<OModel> {
      public string Name => "b";
      public int Order { get; } = -10;
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }
    public sealed class C_Tied : IStoredFormMigration<OModel> {
      public string Name => "c";
      int IStoredFormMigration.Order => 20;
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }
    public sealed class D_Tied : IStoredFormMigration<OModel> {
      public string Name => "d";
      public int Order { get { return 20; } }
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }
    public abstract class Early : IStoredFormMigration<OModel> {
      public abstract string Name { get; }
      public int Order => Orders.SMALL;
      public abstract string BuildSql(StoredFormMigrationTarget target);
    }
    public sealed class E_Inherits : Early {
      public override string Name => "e";
      public override string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    public sealed class F_Arrow : IStoredFormMigration<OModel> {
      public string Name => "f";
      public int Order { get => 25; }
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    // No Order stated: both take the default, 0, and run in class-name order without a warning.
    public sealed class H_Default : IStoredFormMigration<OModel> {
      public string Name => "h";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }
    public sealed class G_Default : IStoredFormMigration<OModel> {
      public string Name => "g";
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    // Another table: the same Order there is no clash.
    public sealed class P_Same : IStoredFormMigration<PModel> {
      public string Name => "p";
      public int Order => 20;
      public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
    }

    [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
    public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
      public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task CustomMigrations_RunByTheirOrder_ThenByClassNameAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(ORDERED);
    await Assert.That(string.Join("\n", result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)))
      .IsEqualTo("");
    var (code, _) = await _runAsync(ORDERED);

    int at(string type) => code.IndexOf($"new global::TestApp.{type}()", StringComparison.Ordinal);
    await Assert.That(at("B_First")).IsGreaterThan(-1);
    await Assert.That(at("B_First")).IsLessThan(at("G_Default"));
    await Assert.That(at("G_Default")).IsLessThan(at("H_Default"))
      .Because("A migration without an Order is at 0, among the others by class name.");
    await Assert.That(at("H_Default")).IsLessThan(at("E_Inherits"));
    await Assert.That(at("B_First")).IsLessThan(at("E_Inherits"))
      .Because("An initializer, a const of another integral type and an inherited Order are all read at build time.");
    await Assert.That(at("E_Inherits")).IsLessThan(at("C_Tied"));
    await Assert.That(at("C_Tied")).IsLessThan(at("D_Tied"))
      .Because("Two migrations with one Order run in class-name order.");
    await Assert.That(at("D_Tied")).IsLessThan(at("F_Arrow"));
    await Assert.That(at("F_Arrow")).IsLessThan(at("A_Last"));
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TwoMigrationsOfOneTableSharingAnOrder_AreWHIZ833_OncePerSharedOrderAsync() {
    var (_, diagnostics) = await _runAsync(ORDERED);

    var shared = diagnostics.Where(d => d.Id == "WHIZ833").ToList();
    await Assert.That(shared).Count().IsEqualTo(1)
      .Because("Only C_Tied and D_Tied state the same Order on one table; G_Default and H_Default state none, and P_Same's table is another.");
    await Assert.That(shared[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = shared[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("TestApp.C_Tied, TestApp.D_Tied");
    await Assert.That(message).Contains("TestApp.OModel");
    await Assert.That(message).Contains("20");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnOrderThatIsNotAConstant_IsWHIZ831_AndTheMigrationIsNotEmittedAsync() {
    const string SOURCE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record VEvent : IEvent;
      public record VModel {
        [StreamId] public Guid Id { get; init; }
      }
      public class VPerspective : IPerspectiveFor<VModel, VEvent> {
        public VModel Apply(VModel currentData, VEvent @event) => currentData;
      }
      public sealed class Computed : IStoredFormMigration<VModel> {
        private static int _next;
        public string Name => "computed";
        public int Order => _next++;
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
      public sealed class Statements : IStoredFormMigration<VModel> {
        public string Name => "statements";
        public int Order { get { var x = 1; return x; } }
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
      public sealed class Settable : IStoredFormMigration<VModel> {
        public string Name => "settable";
        public int Order { get; set; }
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
      public sealed class Unstated : IStoredFormMigration<VModel> {
        public string Name => "unstated";
        public int Order { get; }
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }
      public sealed class Text : IStoredFormMigration<VModel> {
        public string Name => "text";
        public int Order => "x".Length;
        public string BuildSql(StoredFormMigrationTarget target) => "SELECT 1";
      }

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    var (code, diagnostics) = await _runAsync(SOURCE);

    var never = diagnostics.Where(d => d.Id == "WHIZ831").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    await Assert.That(never).Count().IsEqualTo(5);
    await Assert.That(never.All(m => m.Contains("Order is not a compile-time constant", StringComparison.Ordinal))).IsTrue();
    await Assert.That(code).DoesNotContain("StoredFormMigrationSql.Custom(")
      .Because("The build decides the order, so an order it cannot read is a migration it cannot place.");
  }

  private const string INDEXED = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public enum Level { Low, High }

    public record IxEvent : IEvent;

    [PerspectiveIndex(nameof(Rank), nameof(Label))]
    [PerspectiveIndex(nameof(Label), nameof(Id))]
    public record IxModel {
      [StreamId]
      public Guid Id { get; init; }

      [Indexed]
      [Indexed(IndexKinds.Ordered, caseInsensitive: true)]
      [StoredForm(Previously = typeof(int))]
      public string Rank { get; init; } = "";

      [Indexed]
      [StoredForm(Previously = typeof(string))]
      public Level Level { get; init; }

      [StoredForm(Previously = typeof(int))]
      public string Plain { get; init; } = "";

      [Indexed]
      [StoredForm(PreviousName = "Caption")]
      public string Label { get; init; } = "";
    }

    public class IxPerspective : IPerspectiveFor<IxModel, IxEvent> {
      public IxModel Apply(IxModel currentData, IxEvent @event) => currentData;
    }

    public class IxAuditPerspective : IPerspectiveFor<IxModel, IxEvent> {
      public IxModel Apply(IxModel currentData, IxEvent @event) => currentData;
    }

    [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
    public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
      public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ATypeChangeOnAnIndexedKey_ReplacesTheIndexesThatCastIt_BeforeConvertingAsync() {
    var (code, diagnostics) = await _runAsync(INDEXED);
    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ830" or "WHIZ831" or "WHIZ832" or "WHIZ833")).IsEmpty();
    var retry = _retry("TestApp.IxAuditPerspective", "TestApp.IxPerspective");
    string ix(string name, params string[] steps) =>
      $"global::Whizbang.Data.Postgres.StoredFormMigrationSql.Generated(\"testapp\", \"wh_per_ix\", \"wh_per_ix.{name}\", {string.Join(", ", steps)}, {retry}),";

    await Assert.That(code).Contains(ix("Rank:Int32->String",
      $"{STEP}ReplaceIndex(\"Rank\", null, \"idx_ix_rank_json\", \"CREATE INDEX IF NOT EXISTS idx_ix_rank_json ON \\\"testapp\\\".wh_per_ix ((data ->> 'Rank'));\")",
      $"{STEP}ReplaceIndex(\"Rank\", null, \"idx_ix_rank_label\", \"CREATE INDEX IF NOT EXISTS idx_ix_rank_label ON \\\"testapp\\\".wh_per_ix ((data ->> 'Rank'), (data ->> 'Label'));\")",
      $"{STEP}ToText(\"Rank\")"))
      .Because("The field's ordered index and the composite over it are handed over, ahead of the conversion; the folded index casts nothing.");
    await Assert.That(code).Contains(ix("Level:String->Level",
      $"{STEP}ReplaceIndex(\"Level\", \"integer\", \"idx_ix_level_json\", \"CREATE INDEX IF NOT EXISTS idx_ix_level_json ON \\\"testapp\\\".wh_per_ix (((data ->> 'Level')::integer));\")",
      $"{STEP}ToEnumNumber(\"Level\", \"Level\", {NUMBER}Int32, new (string Name, string Value)[] {{ (\"Low\", \"0\"), (\"High\", \"1\") }}, false)"))
      .Because("An enum's index casts its number to the underlying integer.");
    await Assert.That(code).Contains(ix("Plain:Int32->String", $"{STEP}ToText(\"Plain\")"))
      .Because("A key with no index has nothing to replace.");
    await Assert.That(code).Contains(ix("Label:renamed-from:Caption", $"{STEP}Rename(\"Label\", \"Caption\")"))
      .Because("A rename keeps the type, so no index over the key casts to anything old.");
  }
}
