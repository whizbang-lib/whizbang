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
  /// identifier limit, nested types (merged and de-duplicated) and a closed generic model.
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

    public record LineItem(string Sku);
    public record PagedSnapshotModel<TItem>(Guid Id);
    public class LineItemSnapshotPerspective : IPerspectiveFor<PagedSnapshotModel<LineItem>, StockChanged> {
      public PagedSnapshotModel<LineItem> Apply(PagedSnapshotModel<LineItem> currentData, StockChanged @event) => currentData;
    }
    """;

  /// <summary>The names the EF Core driver maps (ToTable), from the model type as the table-naming page documents.</summary>
  private static readonly string[] _efCoreGolden = [
    "wh_per_active_account",
    "wh_per_customer",
    "wh_per_customer_loyalty_tier_summary",
    "wh_per_order",
    "wh_per_paged_snapshot",
    "wh_per_picking",
    "wh_per_product",
    "wh_per_shipment",
    "wh_per_stock_level",
    "wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier",
  ];

  /// <summary>
  /// The names the schema generator (the Dapper driver's DDL and registry payload) creates today. They come from the
  /// PERSPECTIVE class, not the model type the EF Core driver and the table-naming page use, so the two drivers name the
  /// same perspective differently, and the longest name here passes PostgreSQL's 63-byte limit with no diagnostic.
  /// Pinned as they are: changing them renames Dapper tables, and that divergence is tracked separately.
  /// </summary>
  private static readonly string[] _schemaGolden = [
    "wh_per_active_account_active_account",
    "wh_per_customer_loyalty_tier_summary_perspective",
    "wh_per_customer_perspective",
    "wh_per_line_item_snapshot_perspective",
    "wh_per_order_perspective",
    "wh_per_picking",
    "wh_per_product_catalog",
    "wh_per_shipment_perspective",
    "wh_per_stock_level_perspective",
    "wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier_perspective",
  ];

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreDriver_WithDefaultSettings_MapsTheGoldenTableNamesAsync() {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(PROJECT, MSBuildGlobalOptions.For());

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangModelBuilderExtensions.g.cs") ?? "";

    await Assert.That(_golden(_toTable(), generated)).IsEqualTo(string.Join(Environment.NewLine, _efCoreGolden));
    await Assert.That(result.Diagnostics.Select(d => d.Id)).DoesNotContain("WHIZ820")
      .Because("an unset WhizbangMaxIdentifierLength keeps PostgreSQL's 63-byte limit, which the longest name meets exactly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaGenerator_WithDefaultSettings_CreatesTheGoldenTableNamesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(PROJECT, MSBuildGlobalOptions.For());

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(_golden(_createTable(), generated)).IsEqualTo(string.Join(Environment.NewLine, _schemaGolden));
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
