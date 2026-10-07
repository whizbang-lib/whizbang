// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// Remaining decisions in the data layer's pure renderers and folders: the column-type guard on a
/// physical-column retype, an index statement whose name ends the line, an enumeration with no
/// members, and a hook that stamps <c>updated_at</c> with a <see cref="DateTime"/>.
/// </summary>
/// <remarks>No database: every case is string rendering or an in-memory fold.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredFormStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/OptionalExtensionBlocks.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EnumColumnRewriteSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PerEventApplyHooks.cs</code-under-test>
[Category("Shard5")]
public class SqlRenderingBranchTests {

  // The column type is spliced into ALTER TABLE ... TYPE, so the guard is what stands between a
  // generated schema and an injected statement. A blank type, or one carrying anything beyond
  // letters, digits, spaces, parentheses, commas and underscores, is refused.
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  [Arguments("TEXT; DROP TABLE x")]
  [Arguments("TEXT--")]
  public async Task RetypeColumn_ColumnTypeThatIsBlankOrCarriesOtherCharacters_IsRefusedAsync(string columnType) {
    var ex = await Assert.That(() => StoredFormStep.RetypeColumn("amount", columnType, number: null))
      .Throws<ArgumentException>();
    await Assert.That(ex!.ParamName).IsEqualTo("columnType");
  }

  [Test]
  [Arguments("NUMERIC(12, 2)")]
  [Arguments("DOUBLE PRECISION")]
  [Arguments("amount_domain")]
  [Arguments("BIGINT")]
  public async Task RetypeColumn_ColumnTypeOfTheAllowedCharacters_IsAcceptedAsync(string columnType) {
    var step = StoredFormStep.RetypeColumn("amount", columnType, number: null);

    await Assert.That(step).IsNotNull();
  }

  // A trailing statement with no ON clause on the same line still names its index; a reader that
  // needed a following space would drop the last index of a block from the rebuild list.
  [Test]
  public async Task IndexNames_NameAtTheEndOfTheLine_IsReadWholeAsync() {
    var names = OptionalExtensionBlocks.IndexNames("CREATE INDEX IF NOT EXISTS idx_first ON t (a);\nCREATE INDEX idx_last");

    await Assert.That(names.Length).IsEqualTo(2);
    await Assert.That(names[0]).IsEqualTo("idx_first");
    await Assert.That(names[1]).IsEqualTo("idx_last");
  }

  // An enumeration with no members still renders a valid statement: an empty IN list is a syntax
  // error in PostgreSQL, so the unreadable-value test lists NULL instead.
  [Test]
  public async Task EnumRewrite_EnumerationWithNoMembers_RendersAValidInListAsync() {
    var sql = EnumColumnRewriteSql.Build("public", "orders", "status", "OrderStatus", "integer", []);

    await Assert.That(sql).Contains("NOT IN (NULL)");
    await Assert.That(sql).DoesNotContain("NOT IN ()");
  }

  [Test]
  public async Task UpdatedAtHook_UtcDateTime_IsTheSameInstantAsync() {
    var stamp = new DateTime(2031, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    var plan = PerEventApplyHooks.Resolve(_registryStamping(stamp), _context());

    await Assert.That(plan.UpdatedAt).IsEqualTo(new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero));
  }

  // A DateTime with no kind (what a database read or a parse often yields) is taken as UTC, not as
  // the host's local time: the stamp must not move with the server's time zone.
  [Test]
  public async Task UpdatedAtHook_UnspecifiedDateTime_IsReadAsUtcAsync() {
    var stamp = new DateTime(2031, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

    var plan = PerEventApplyHooks.Resolve(_registryStamping(stamp), _context());

    await Assert.That(plan.UpdatedAt).IsEqualTo(new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero));
  }

  private static ApplyHookRegistry _registryStamping(DateTime stamp) =>
    WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new StampHook(stamp), key: WhizbangApplyHookKeys.TIMESTAMPS);

  private static ApplyHookContext _context() => new() {
    ModelType = typeof(StampedModel),
    ApplyTimestamp = DateTimeOffset.UnixEpoch,
  };

  private sealed class StampedModel;

  private sealed class StampHook(DateTime stamp) : IApplyHook<object> {
    public void Configure(IApplyHookBuilder<object> builder, ApplyHookContext context) =>
      builder.SetColumn(ApplyHookColumns.UPDATED_AT, stamp);
  }
}
