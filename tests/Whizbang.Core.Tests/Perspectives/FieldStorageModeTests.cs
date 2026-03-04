using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="FieldStorageMode"/> enum.
/// </summary>
public class FieldStorageModeTests {
  [Test]
  public async Task FieldStorageMode_JsonOnly_IsDefinedAsync() {
    var value = FieldStorageMode.JsonOnly;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task FieldStorageMode_Extracted_IsDefinedAsync() {
    var value = FieldStorageMode.Extracted;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task FieldStorageMode_Split_IsDefinedAsync() {
    var value = FieldStorageMode.Split;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task FieldStorageMode_HasThreeValuesAsync() {
    var values = Enum.GetValues<FieldStorageMode>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task FieldStorageMode_JsonOnly_HasCorrectIntValueAsync() {
    var value = (int)FieldStorageMode.JsonOnly;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task FieldStorageMode_Extracted_HasCorrectIntValueAsync() {
    var value = (int)FieldStorageMode.Extracted;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task FieldStorageMode_Split_HasCorrectIntValueAsync() {
    var value = (int)FieldStorageMode.Split;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task FieldStorageMode_JsonOnly_IsDefaultAsync() {
    // JsonOnly should be the default (0) for backwards compatibility
    var value = default(FieldStorageMode);
    await Assert.That(value).IsEqualTo(FieldStorageMode.JsonOnly);
  }
}
