// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The shared perspective table-naming rule (#1719): a manual name is used as written, a generated one follows the
/// suffix settings, and the checks both generators report through.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming</docs>
public class PerspectiveTableNamingTests {
  [Test]
  public async Task TableName_WithoutAManualName_GeneratesFromTheBaseNameAsync() {
    var source = new PerspectiveTableSource("OrderModel", null);

    await Assert.That(source.TableName(TableNameConfig.Default)).IsEqualTo("wh_per_order");
    await Assert.That(source.TableName(TableNameConfig.NoStripping)).IsEqualTo("wh_per_order_model");
    await Assert.That(source.IsManual).IsFalse();
  }

  [Test]
  public async Task TableName_WithAManualName_UsesItWhateverTheSettingsAsync() {
    var source = new PerspectiveTableSource("OrderModel", "wh_per_orders_model");

    await Assert.That(source.TableName(TableNameConfig.Default)).IsEqualTo("wh_per_orders_model");
    await Assert.That(source.TableName(TableNameConfig.NoStripping)).IsEqualTo("wh_per_orders_model");
    await Assert.That(source.IsManual).IsTrue();
  }

  [Test]
  [Arguments("wh_per_orders", true)]
  [Arguments("wh_per_order_2024", true)]
  [Arguments("wh_per__", true)]
  [Arguments("wh_per_", false)]
  [Arguments("orders", false)]
  [Arguments("wh_per_Orders", false)]
  [Arguments("wh_per_order-lines", false)]
  [Arguments("wh_per_ordér", false)]
  [Arguments("", false)]
  public async Task IsValidManualName_AcceptsOnlyThePrefixAndLowercaseAsciiAsync(string name, bool valid) {
    await Assert.That(PerspectiveTableNaming.IsValidManualName(name)).IsEqualTo(valid);
  }

  [Test]
  [Arguments("Shop.OrderPerspective", "wh_per_order", "wh_per_order_perspective")]
  [Arguments("OrderPerspective", "wh_per_order", "wh_per_order_perspective")]
  [Arguments("Shop.ProductCatalogProjection", "wh_per_product", "wh_per_product_catalog")]
  [Arguments("Shop.Picking+Projection", "wh_per_picking", null)]
  [Arguments("Shop.WarehouseReplenishmentForecastAccuracyByRegionTierPerspective",
    "wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier", null)]
  public async Task FormerDapperTableName_IsThePerspectiveClassName_AsPostgresStoredIt_OrNullWhenUnchangedAsync(
      string perspectiveClrTypeName, string tableName, string? former) {
    await Assert.That(PerspectiveTableNaming.FormerDapperTableName(perspectiveClrTypeName, tableName, TableNameConfig.Default))
      .IsEqualTo(former)
      .Because("a released Dapper build named the table from the perspective class, and PostgreSQL truncated a name past 63 bytes");
  }

  [Test]
  [Arguments("wh_per_order", "wh_per_order")]
  [Arguments("wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier_perspective",
    "wh_per_warehouse_replenishment_forecast_accuracy_by_region_tier")]
  [Arguments("wh_per_éééééééééééééééééééééééééééééééééééééééé", "wh_per_éééééééééééééééééééééééééééé")]
  [Arguments("wh_per_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\uD83D\uDE00", "wh_per_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
  public async Task AsStored_ClipsToPostgresIdentifierLimit_OnACharacterBoundaryAsync(string name, string stored) {
    await Assert.That(PerspectiveTableNaming.AsStored(name)).IsEqualTo(stored);
  }

  [Test]
  public async Task Collisions_ReportOneErrorPerSharedTable_NotPerPerspectiveOfOneModelAsync() {
    var tables = new[] {
      ("wh_per_order", "Shop.OrderModel", (Location?)Location.None),
      ("wh_per_order", "Shop.OrderModel", (Location?)Location.None),
      ("wh_per_order", "Shop.OrderDto", Location.None),
      ("wh_per_invoice", "Shop.InvoiceModel", Location.None),
      ("wh_per_invoice", "Shop.InvoiceModel", Location.None),
    };

    var diagnostics = PerspectiveTableNaming.Collisions(tables).ToList();

    await Assert.That(diagnostics.Count).IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ825");
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("'wh_per_order' is the table of more than one model (Shop.OrderDto, Shop.OrderModel)");
  }
}
