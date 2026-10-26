// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #1719: a perspective's table name, generated from its model or set with <c>[PerspectiveTable]</c>, is checked
/// at build time on both drivers. A name past PostgreSQL's 63-byte limit, a manual name that is not a valid table name,
/// and two models resolving to one table are compile errors, never a table PostgreSQL silently truncates.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming#explicit-table-names</docs>
[Category("SourceGenerators")]
public class PerspectiveTableNamingDiagnosticsTests {
  // 7 bytes of prefix and 57 of name: 64 bytes, one over the limit.
  private const string OVER_LIMIT_MODEL = "Zqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";

  private static string _perspective(string model, string attribute = "", string perspective = "") => $$"""
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace Shop;

    public record Noted : IEvent;

    {{attribute}}
    public record {{model}}(Guid Id);

    public class {{(perspective.Length == 0 ? model + "Perspective" : perspective)}} : IPerspectiveFor<{{model}}, Noted> {
      public {{model}} Apply({{model}} currentData, Noted @event) => currentData;
    }
    """;

  private static GeneratorDriverRunResult _schema(string source) =>
    GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source, MSBuildGlobalOptions.For());

  private static GeneratorDriverRunResult _efCore(string source) =>
    GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source, MSBuildGlobalOptions.For());

  private static string _generated(GeneratorDriverRunResult result) =>
    GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithAGeneratedNameOverTheLimit_ReportsWHIZ823AndCreatesNoTableAsync() {
    var result = _schema(_perspective(OVER_LIMIT_MODEL));

    var error = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ823");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = error.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("wh_per_zqqq");
    await Assert.That(message).Contains("64 bytes");
    await Assert.That(message).Contains("63");
    await Assert.That(message).Contains("[PerspectiveTable]");
    await Assert.That(error.Location.IsInSource).IsTrue().Because("the error points at the model");
    await Assert.That(_generated(result)).DoesNotContain("CREATE TABLE IF NOT EXISTS wh_per_zqqq");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithANameAtTheLimit_ReportsNothingAsync() {
    var result = _schema(_perspective(OVER_LIMIT_MODEL[..^1]));

    await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithAManualNameOverTheLimit_ReportsWHIZ823Async() {
    var manual = "wh_per_" + new string('a', 57);

    var result = _schema(_perspective("Backlog", $"[PerspectiveTable(\"{manual}\")]"));

    await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("WHIZ823");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments("Orders")]
  [Arguments("orders")]
  [Arguments("wh_per_")]
  [Arguments("wh_per_Orders")]
  [Arguments("wh_per_order-lines")]
  [Arguments("wh_per_order lines")]
  [Arguments("")]
  public async Task SchemaGenerator_WithAnInvalidManualName_ReportsWHIZ824Async(string manual) {
    var result = _schema(_perspective("Backlog", $"[PerspectiveTable(\"{manual}\")]"));

    var error = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ824");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(error.GetMessage(CultureInfo.InvariantCulture)).Contains("wh_per_");
    await Assert.That(_generated(result)).DoesNotContain("CREATE TABLE");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithANullManualName_ReportsWHIZ824Async() {
    var result = _schema(_perspective("Backlog", "[PerspectiveTable(null!)]"));

    await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("WHIZ824");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithTwoModelsResolvingToOneTable_ReportsWHIZ825Async() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace Shop;

      public record Noted : IEvent;
      public record OrderDto(Guid Id);
      public record OrderModel(Guid Id);
      public class OrderListPerspective : IPerspectiveFor<OrderDto, Noted> {
        public OrderDto Apply(OrderDto currentData, Noted @event) => currentData;
      }
      public class OrderAdminPerspective : IPerspectiveFor<OrderModel, Noted> {
        public OrderModel Apply(OrderModel currentData, Noted @event) => currentData;
      }
      """;

    var result = _schema(source);

    var error = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ825");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = error.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("wh_per_order");
    await Assert.That(message).Contains("Shop.OrderDto");
    await Assert.That(message).Contains("Shop.OrderModel");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithTwoPerspectivesOfOneModel_CreatesItsTableOnceAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace Shop;

      public record Noted : IEvent;
      public record OrderModel(Guid Id);
      public class OrderListPerspective : IPerspectiveFor<OrderModel, Noted> {
        public OrderModel Apply(OrderModel currentData, Noted @event) => currentData;
      }
      public class OrderAuditPerspective : IPerspectiveFor<OrderModel, Noted> {
        public OrderModel Apply(OrderModel currentData, Noted @event) => currentData;
      }
      """;

    var result = _schema(source);
    var generated = _generated(result);

    await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty()
      .Because("one model folded by two perspectives shares one table, as on the EF Core driver");
    var creates = generated.Split("CREATE TABLE IF NOT EXISTS wh_per_order (").Length - 1;
    await Assert.That(creates).IsEqualTo(2)
      .Because("one entry creates it, and the concatenated script repeats that entry once");
    await Assert.That(generated).Contains("OrderListPerspective");
    await Assert.That(generated).Contains("OrderAuditPerspective")
      .Because("both perspectives are still recorded in the registry payload");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_InAnEFCoreConsumer_LeavesTheNameChecksToTheEFCoreGeneratorAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation(_perspective(OVER_LIMIT_MODEL))
      .AddReferences(CSharpCompilation.Create("Whizbang.Data.EFCore.Postgres").ToMetadataReference());

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(compilation, MSBuildGlobalOptions.For());

    await Assert.That(result.Diagnostics.Select(d => d.Id)).DoesNotContain("WHIZ823")
      .Because("the EF Core generator reports WHIZ820 for the same name, and one error is enough");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithAWhizbangMaxIdentifierLength_UsesItAsTheLimitAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _perspective("OrderLine"), MSBuildGlobalOptions.For(new Dictionary<string, string> { ["WhizbangMaxIdentifierLength"] = "16" }));

    await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("WHIZ823")
      .Because("wh_per_order_line is 17 bytes, over the configured 16");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreGenerator_WithAnInvalidManualName_ReportsWHIZ824AndMapsNoTableAsync() {
    var result = _efCore(_perspective("Backlog", "[PerspectiveTable(\"Backlog\")]"));

    var error = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ824");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangModelBuilderExtensions.g.cs") ?? "";
    await Assert.That(generated).DoesNotContain("ToTable(\"Backlog\"");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreGenerator_WithAManualNameOverTheLimit_ReportsWHIZ820NamingTheAttributeAsync() {
    var manual = "wh_per_" + new string('a', 57);

    var result = _efCore(_perspective("Backlog", $"[PerspectiveTable(\"{manual}\")]"));

    var error = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.GetMessage(CultureInfo.InvariantCulture)).Contains("[PerspectiveTable]");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreGenerator_WithTwoModelsResolvingToOneTable_ReportsWHIZ825Async() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace Shop;

      public record Noted : IEvent;
      [PerspectiveTable("wh_per_orders")]
      public record OrderDto(Guid Id);
      [PerspectiveTable("wh_per_orders")]
      public record OrderModel(Guid Id);
      public class OrderListPerspective : IPerspectiveFor<OrderDto, Noted> {
        public OrderDto Apply(OrderDto currentData, Noted @event) => currentData;
      }
      public class OrderAdminPerspective : IPerspectiveFor<OrderModel, Noted> {
        public OrderModel Apply(OrderModel currentData, Noted @event) => currentData;
      }
      """;

    var result = _efCore(source);

    await Assert.That(result.Diagnostics.Count(d => d.Id == "WHIZ825")).IsEqualTo(1);
  }
}
