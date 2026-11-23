// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The type mapping of a strongly-typed id (#2518): a uuid scalar whose JSON is the UUID string, reading the object
/// form an earlier release stored.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdTypeMapping.cs</code-under-test>
[Category("Shard1")]
public class WhizbangIdTypeMappingTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

  [Test]
  public async Task RegisteredId_MapsToAUuidScalarAsync() {
    var mapping = WhizbangIdTypeMapping.For(typeof(TestOrderId));

    await Assert.That(mapping).IsNotNull();
    await Assert.That(mapping!.ClrType).IsEqualTo(typeof(TestOrderId));
    await Assert.That(mapping.StoreType).IsEqualTo(WhizbangIdTypeMapping.STORE_TYPE);
    await Assert.That(mapping.Converter!.ProviderClrType).IsEqualTo(typeof(Guid));
    await Assert.That(WhizbangIdTypeMapping.For(typeof(TestOrderId))).IsSameReferenceAs(mapping);
  }

  /// <summary>An id a query inlines as a constant renders as a quoted uuid, not as bare text the parser refuses.</summary>
  [Test]
  public async Task SqlLiteral_IsAQuotedUuidAsync() {
    var mapping = WhizbangIdTypeMapping.For(typeof(TestOrderId))!;

    await Assert.That(mapping.GenerateSqlLiteral(new TestOrderId(Guid.Parse(V4)))).IsEqualTo($"'{V4}'::uuid");
  }

  [Test]
  public async Task NotAnId_HasNoMappingAsync() {
    await Assert.That(WhizbangIdTypeMapping.For(typeof(string))).IsNull();
    await Assert.That(new WhizbangIdTypeMappingSourcePlugin().FindMapping(new RelationalTypeMappingInfo(typeof(int)))).IsNull();
  }

  [Test]
  public async Task Plugin_AnswersForARegisteredIdAsync() {
    var mapping = new WhizbangIdTypeMappingSourcePlugin().FindMapping(new RelationalTypeMappingInfo(typeof(TestOrderId)));

    await Assert.That(mapping).IsTypeOf<WhizbangIdTypeMapping>();
  }

  /// <summary>The converter loads through the unchecked constructor: a stored v4 loads on a strict id.</summary>
  [Test]
  public async Task Converter_LoadsUncheckedAndStoresTheGuidAsync() {
    var converter = WhizbangIdTypeMapping.For(typeof(TestOrderId))!.Converter!;
    var v4 = Guid.Parse(V4);

    var loaded = (TestOrderId)converter.ConvertFromProvider(v4)!;

    await Assert.That(loaded.Value).IsEqualTo(v4);
    await Assert.That(converter.ConvertToProvider(loaded)).IsEqualTo(v4);
  }

  [Test]
  public async Task JsonReaderWriter_WritesTheUuidStringAndReadsBothFormsAsync() {
    var readerWriter = WhizbangIdJsonReaderWriter<TestOrderId>.Instance;
    var id = TestOrderId.New();

    var json = readerWriter.ToJsonString(id);
    var fromString = (TestOrderId)readerWriter.FromJsonString(json);
    var fromObject = (TestOrderId)readerWriter.FromJsonString($"{{\"Value\":\"{id.Value}\"}}");
    var fromObjectWithExtra = (TestOrderId)readerWriter.FromJsonString($"{{\"Other\":1,\"Value\":\"{id.Value}\"}}");

    await Assert.That(json).IsEqualTo($"\"{id.Value}\"");
    await Assert.That(fromString).IsEqualTo(id);
    await Assert.That(fromObject).IsEqualTo(id);
    await Assert.That(fromObjectWithExtra).IsEqualTo(id);
  }

  [Test]
  public async Task JsonReaderWriter_RefusesAnObjectWithoutValueOrANumberAsync() {
    var readerWriter = WhizbangIdJsonReaderWriter<TestOrderId>.Instance;

    await Assert.That(() => readerWriter.FromJsonString("{\"Other\":1}")).Throws<System.Text.Json.JsonException>();
    await Assert.That(() => readerWriter.FromJsonString("42")).Throws<System.Text.Json.JsonException>();
  }
}
