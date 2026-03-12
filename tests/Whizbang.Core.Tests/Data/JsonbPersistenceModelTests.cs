using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;

namespace Whizbang.Core.Tests.Data;

public class JsonbPersistenceModelTests {
  [Test]
  public async Task DefaultModel_HasEmptyStringsAsync() {
    var model = new JsonbPersistenceModel();

    await Assert.That(model.DataJson).IsEqualTo(string.Empty);
    await Assert.That(model.MetadataJson).IsEqualTo(string.Empty);
    await Assert.That(model.ScopeJson).IsNull();
  }

  [Test]
  public async Task DataSizeBytes_CalculatesUtf8SizeAsync() {
    var model = new JsonbPersistenceModel { DataJson = "{\"key\":\"value\"}" };

    await Assert.That(model.DataSizeBytes).IsEqualTo(15);
  }

  [Test]
  public async Task MetadataSizeBytes_CalculatesUtf8SizeAsync() {
    var model = new JsonbPersistenceModel { MetadataJson = "{}" };

    await Assert.That(model.MetadataSizeBytes).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeSizeBytes_WhenNull_ReturnsZeroAsync() {
    var model = new JsonbPersistenceModel { ScopeJson = null };

    await Assert.That(model.ScopeSizeBytes).IsEqualTo(0);
  }

  [Test]
  public async Task ScopeSizeBytes_WhenSet_CalculatesUtf8SizeAsync() {
    var model = new JsonbPersistenceModel { ScopeJson = "{\"tenant\":\"abc\"}" };

    await Assert.That(model.ScopeSizeBytes).IsGreaterThan(0);
  }

  [Test]
  public async Task TotalSizeBytes_SumsAllColumnsAsync() {
    var model = new JsonbPersistenceModel {
      DataJson = "{}",
      MetadataJson = "{}",
      ScopeJson = "{}"
    };

    await Assert.That(model.TotalSizeBytes).IsEqualTo(6);
  }

  [Test]
  public async Task TotalSizeBytes_WithUnicodeChars_CalculatesCorrectlyAsync() {
    // Unicode characters take more bytes in UTF-8
    var model = new JsonbPersistenceModel { DataJson = "{\"emoji\":\"🎉\"}" };

    // "🎉" is 4 bytes in UTF-8
    await Assert.That(model.DataSizeBytes).IsGreaterThan(13);
  }
}
