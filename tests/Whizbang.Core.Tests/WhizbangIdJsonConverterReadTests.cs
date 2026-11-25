// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// A generated id's JSON converter reads the UUID string and the object <c>{"Value": "..."}</c> an Entity Framework
/// perspective stores until its fleet reads the UUID string, so a reader on either driver (the Dapper store reads with
/// these options) reads a row in either form (#2518).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/WhizbangIdJsonSnippets.cs</code-under-test>
public class WhizbangIdJsonConverterReadTests {
  private static readonly JsonSerializerOptions _options = new() { Converters = { new RoutedThingIdJsonConverter() } };

  [Test]
  public async Task Read_TakesTheUuidString_AndTheObjectForm_AndWritesTheUuidStringAsync() {
    var id = RoutedThingId.New();

    var fromString = JsonSerializer.Deserialize<RoutedThingId>($"\"{id.Value}\"", _options);
    var fromObject = JsonSerializer.Deserialize<RoutedThingId>($"{{\"Value\":\"{id.Value}\"}}", _options);
    var besideOthers = JsonSerializer.Deserialize<RoutedThingId>($"{{\"Other\":{{\"a\":[1]}},\"Value\":\"{id.Value}\"}}", _options);

    await Assert.That(fromString).IsEqualTo(id);
    await Assert.That(fromObject).IsEqualTo(id);
    await Assert.That(besideOthers).IsEqualTo(id);
    await Assert.That(JsonSerializer.Serialize(id, _options)).IsEqualTo($"\"{id.Value}\"");
  }

  [Test]
  public async Task Read_AnObjectWithNoValue_IsRefusedAsync() {
    var refused = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoutedThingId>("{\"Other\":1}", _options));

    await Assert.That(refused.Message).Contains("RoutedThingId object holds no Value");
  }

  [Test]
  public async Task Read_TheObjectForm_IsCheckedByTheIdsRule_AsTheUuidStringIsAsync() {
    var v4 = Guid.NewGuid();

    await Assert.That(() => JsonSerializer.Deserialize<RoutedThingId>($"{{\"Value\":\"{v4}\"}}", _options)).Throws<ArgumentException>();
    await Assert.That(() => JsonSerializer.Deserialize<RoutedThingId>($"\"{v4}\"", _options)).Throws<ArgumentException>();
  }
}
