// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The type mapping of a strongly-typed id (#2518): a uuid scalar whose JSON is the UUID string.
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
  public async Task JsonReaderWriter_WritesAndReadsTheUuidStringAsync() {
    var readerWriter = WhizbangIdJsonReaderWriter<TestOrderId>.Instance;
    var id = TestOrderId.New();

    var json = readerWriter.ToJsonString(id);

    await Assert.That(json).IsEqualTo($"\"{id.Value}\"");
    await Assert.That((TestOrderId)readerWriter.FromJsonString(json)).IsEqualTo(id);
  }

  [Test]
  public async Task JsonReaderWriter_RefusesAnythingButAUuidStringAsync() {
    var readerWriter = WhizbangIdJsonReaderWriter<TestOrderId>.Instance;

    await Assert.That(() => readerWriter.FromJsonString($"{{\"Value\":\"{TestOrderId.New().Value}\"}}"))
      .Throws<System.Text.Json.JsonException>()
      .WithMessageContaining("TestOrderId must be a UUID string, but the document holds StartObject");
    await Assert.That(() => readerWriter.FromJsonString("42")).Throws<System.Text.Json.JsonException>();
  }
}
