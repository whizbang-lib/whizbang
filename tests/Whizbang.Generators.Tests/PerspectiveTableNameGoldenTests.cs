// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The perspective table names a representative project gets with default settings, pinned exactly, so any change to
/// naming fails here loudly. The build properties are the shape MSBuild delivers: declared by the packages and left
/// unset, which reaches the generators as empty values. The unit tests fed a missing property or a set one, and an
/// empty one renamed every table in a consumer that changed nothing (#1660).
/// </summary>
/// <docs>fundamentals/perspectives/table-naming</docs>
[Category("SourceGenerators")]
public partial class PerspectiveTableNameGoldenTests {
  /// <summary>
  /// One model per default suffix (ReadModel, Model, Projection, Dto, View), a multi-word name, a name at the 63-byte
  /// identifier limit, nested types (merged and de-duplicated), a closed generic model, and a model named with
  /// <c>[PerspectiveTable]</c>.
  /// </summary>
  private const string PROJECT = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace Shop.Fulfillment;

    public record StockChanged : IEvent;

    public record OrderModel(Guid Id);
    public class OrderPerspective : IPerspectiveFor<OrderModel, StockChanged> {
      public OrderModel Apply(OrderModel currentData, StockChanged @event) => currentData;
    }

    public record StockLevelProjection(Guid Id);
    public class StockLevelPerspective : IPerspectiveFor<StockLevelProjection, StockChanged> {
      public StockLevelProjection Apply(StockLevelProjection currentData, StockChanged @event) => currentData;
    }

    public record CustomerDto(Guid Id);
    public class CustomerPerspective : IPerspectiveFor<CustomerDto, StockChanged> {
      public CustomerDto Apply(CustomerDto currentData, StockChanged @event) => currentData;
    }

    public record ProductReadModel(Guid Id);
    public class ProductCatalogProjection : IPerspectiveFor<ProductReadModel, StockChanged> {
      public ProductReadModel Apply(ProductReadModel currentData, StockChanged @event) => currentData;
    }

    public record ShipmentView(Guid Id);
    public class ShipmentPerspective : IPerspectiveFor<ShipmentView, StockChanged> {
      public ShipmentView Apply(ShipmentView currentData, StockChanged @event) => currentData;
    }

    public record CustomerLoyaltyTierSummaryModel(Guid Id);
    public class CustomerLoyaltyTierSummaryPerspective : IPerspectiveFor<CustomerLoyaltyTierSummaryModel, StockChanged> {
      public CustomerLoyaltyTierSummaryModel Apply(CustomerLoyaltyTierSummaryModel currentData, StockChanged @event) => currentData;
    }

    public record WarehouseReplenishmentForecastAccuracyByRegionTierModel(Guid Id);
    public class WarehouseReplenishmentForecastAccuracyByRegionTierPerspective
      : IPerspectiveFor<WarehouseReplenishmentForecastAccuracyByRegionTierModel, StockChanged> {
      public WarehouseReplenishmentForecastAccuracyByRegionTierModel Apply(
        WarehouseReplenishmentForecastAccuracyByRegionTierModel currentData, StockChanged @event) => currentData;
    }

    public static class Picking {
      public record Model(Guid Id);
      public class Projection : IPerspectiveFor<Model, StockChanged> {
        public Model Apply(Model currentData, StockChanged @event) => currentData;
      }
    }

    public static class ActiveAccount {
      public record ActiveAccountModel(Guid Id);
      public class ActiveAccountProjection : IPerspectiveFor<ActiveAccountModel, StockChanged> {
        public ActiveAccountModel Apply(ActiveAccountModel currentData, StockChanged @event) => currentData;
      }
    }

    [PerspectiveTable("wh_per_fulfillment_backlog")]
    public record BacklogSnapshotModel(Guid Id);
    public class BacklogPerspective : IPerspectiveFor<BacklogSnapshotModel, StockChanged> {
      public BacklogSnapshotModel Apply(BacklogSnapshotModel currentData, StockChanged @event) => currentData;
    }

    public record LineItem(string Sku);
    public record PagedSnapshotModel<TItem>(Guid Id);
    public class LineItemSnapshotPerspective : IPerspectiveFor<PagedSnapshotModel<LineItem>, StockChanged> {
      public PagedSnapshotModel<LineItem> Apply(PagedSnapshotModel<LineItem> currentData, StockChanged @event) => currentData;
    }
    """;

  /// <summary>
  /// The names both drivers use: the EF Core driver maps them (ToTable) and the schema generator (the Dapper driver's
  /// DDL and registry payload) creates them, from the model type as the table-naming page documents (#1719).
  /// </summary>
  private static readonly string[] _goldenNames = [
    "wh_per_active_account",
    "wh_per_customer",
    "wh_per_customer_loyalty_tier_summary",
    "wh_per_fulfillment_backlog",
    "wh_per_order",
    "wh_per_paged_snapshot",
    "wh_per_picking",
    "wh_per_product",
    "wh_per_shipment",
    "wh_per_stock_level",
    "wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier",
  ];

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreDriver_WithDefaultSettings_MapsTheGoldenTableNamesAsync() {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(PROJECT, MSBuildGlobalOptions.For());

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangModelBuilderExtensions.g.cs") ?? "";

    await Assert.That(_golden(_toTable(), generated)).IsEqualTo(string.Join(Environment.NewLine, _goldenNames));
    await Assert.That(result.Diagnostics.Select(d => d.Id)).DoesNotContain("WHIZ820")
      .Because("an unset WhizbangMaxIdentifierLength keeps PostgreSQL's 63-byte limit, which the longest name meets exactly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithDefaultSettings_CreatesTheGoldenTableNamesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(PROJECT, MSBuildGlobalOptions.For());

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(_golden(_createTable(), generated)).IsEqualTo(string.Join(Environment.NewLine, _goldenNames));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task BothDrivers_WithDefaultSettings_NameEveryModelTheSameAsync() {
    var efCore = GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(PROJECT, MSBuildGlobalOptions.For()),
      "WhizbangModelBuilderExtensions.g.cs") ?? "";
    var schema = GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(PROJECT, MSBuildGlobalOptions.For()),
      "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(_golden(_createTable(), schema)).IsEqualTo(_golden(_toTable(), efCore))
      .Because("one model has one table whichever driver builds it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task BothDrivers_WithSuffixStrippingOff_KeepTheManualNameAsync() {
    var options = MSBuildGlobalOptions.For(new Dictionary<string, string> { ["WhizbangStripTableNameSuffixes"] = "false" });

    var efCore = GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(PROJECT, options),
      "WhizbangModelBuilderExtensions.g.cs") ?? "";
    var schema = GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(PROJECT, options), "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(_golden(_toTable(), efCore)).Contains("wh_per_fulfillment_backlog");
    await Assert.That(_golden(_createTable(), schema)).Contains("wh_per_fulfillment_backlog");
    await Assert.That(_golden(_toTable(), efCore)).Contains("wh_per_order_model")
      .Because("the setting still applies to the generated names");
  }

  // One name per line, sorted, so a failure prints the whole list it found next to the golden one.
  private static string _golden(Regex pattern, string generated) =>
    string.Join(Environment.NewLine, pattern.Matches(generated)
      .Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

  [GeneratedRegex("ToTable\\(\"(?<name>[a-z0-9_]+)\"")]
  private static partial Regex _toTable();

  [GeneratedRegex("CREATE TABLE IF NOT EXISTS (?<name>[a-z0-9_]+) \\(")]
  private static partial Regex _createTable();
}
