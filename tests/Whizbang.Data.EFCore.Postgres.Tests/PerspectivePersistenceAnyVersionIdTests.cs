// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A <c>[WhizbangId(AcceptAnyVersion = true)]</c> id stored in a perspective document reads back whatever its
/// Guid version, as the UUID string both writers store (#2518). A row whose
/// id field was never set holds an empty id; it loads, because the load path (the constructor and the
/// <c>Value</c> initializer) stays unchecked on every id.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("Shard1")]
public class PerspectivePersistenceAnyVersionIdTests {
  private static JsonSerializerOptions _options() => PerspectivePersistenceJsonContext.CreateOptions();

  [Test]
  [Arguments("3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
  [Arguments("886313e1-3b8a-5372-9b90-0c9aee199e5d")]
  [Arguments("019e244a-6bda-78a9-a08f-a1011c9c31dd")]
  [Arguments("a1b2c3d4-e5f6-8789-8abc-def012345678")]
  public async Task AnyVersionId_RoundTripsThroughThePersistenceProfileAsync(string text) {
    var id = TestAnyVersionId.From(Guid.Parse(text));
    var typeInfo = _options().GetTypeInfo(typeof(TestAnyVersionId));

    var json = JsonSerializer.Serialize(id, typeInfo);
    var read = (TestAnyVersionId)JsonSerializer.Deserialize(json, typeInfo)!;

    // The UUID string, the form Entity Framework writes under the id's scalar mapping (#2518).
    await Assert.That(json).IsEqualTo($"\"{text}\"");
    await Assert.That(read).IsEqualTo(id);
  }

  [Test]
  public async Task AnyVersionId_NeverSetStoredValue_LoadsAsync() {
    var typeInfo = _options().GetTypeInfo(typeof(TestAnyVersionId));
    var json = $"\"{Guid.Empty}\"";

    var loaded = (TestAnyVersionId)JsonSerializer.Deserialize(json, typeInfo)!;

    await Assert.That(loaded.Value).IsEqualTo(Guid.Empty);
  }
}
