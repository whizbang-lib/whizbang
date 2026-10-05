// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The registry a store consults to load a Split model's promoted fields: a registered model hands back its
/// columns and the generated copy, and any other model has none.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/SplitPhysicalFieldRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/SplitPhysicalFieldMap.cs</code-under-test>
public class SplitPhysicalFieldRegistryTests {
  private sealed class RegisteredModel {
    public string? Status { get; set; }
  }

  private sealed class UnregisteredModel;

  private sealed class FixedReader(string value) : IPhysicalColumnReader {
    public T Read<T>(string column) => (T)(object)$"{column}={value}";
    public float[]? GetVector(string column) => null;
  }

  [Test]
  public async Task TryGet_ARegisteredModel_ReturnsItsColumnsAndCopyAsync() {
    SplitPhysicalFieldRegistry.Register(new SplitPhysicalFieldMap<RegisteredModel>(
        [new SplitPhysicalColumn("status", IsVector: false)],
        static (model, read) => {
          model.Status = read.Read<string?>("status");
          return model;
        }));

    var found = SplitPhysicalFieldRegistry.TryGet<RegisteredModel>(out var map);

    await Assert.That(found).IsTrue();
    await Assert.That(map!.Columns).IsEquivalentTo([new SplitPhysicalColumn("status", false)]);
    var model = map.Hydrate(new RegisteredModel(), new FixedReader("x"));
    await Assert.That(model.Status).IsEqualTo("status=x");
  }

  [Test]
  public async Task TryGet_AModelWithNoRegistration_IsFalseAsync() {
    var found = SplitPhysicalFieldRegistry.TryGet<UnregisteredModel>(out var map);

    await Assert.That(found).IsFalse();
    await Assert.That(map).IsNull();
  }

  [Test]
  public async Task Register_ANullMap_ThrowsAsync() {
    await Assert.That(() => SplitPhysicalFieldRegistry.Register<RegisteredModel>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Map_WithoutColumnsOrCopy_ThrowsAsync() {
    await Assert.That(() => new SplitPhysicalFieldMap<RegisteredModel>(null!, static (model, _) => model))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new SplitPhysicalFieldMap<RegisteredModel>([], null!))
      .Throws<ArgumentNullException>();
  }
}
