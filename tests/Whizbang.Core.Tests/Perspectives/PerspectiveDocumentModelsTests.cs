// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The model type each perspective table stores, registered by every assembly's generated schema at module load
/// (#1874): what a driver with no model of its own (the Dapper driver) reads the declared names of a table's
/// documents from, with no reflection.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveDocumentModels.cs</code-under-test>
[NotInParallel(nameof(PerspectiveDocumentModels))]
public class PerspectiveDocumentModelsTests {
  private sealed class First;
  private sealed class Second;

  [Test]
  public async Task Register_ThenRegistered_ReturnsTheModelUnderItsTableAsync() {
    var table = $"wh_per_{Guid.NewGuid():N}";

    PerspectiveDocumentModels.Register(table, typeof(First));

    await Assert.That(PerspectiveDocumentModels.Registered.Single(m => m.Table == table).Model).IsEqualTo(typeof(First));
  }

  [Test]
  public async Task Register_SameTableTwice_KeepsOneModel_TheLatestAsync() {
    var table = $"wh_per_{Guid.NewGuid():N}";

    PerspectiveDocumentModels.Register(table, typeof(First));
    PerspectiveDocumentModels.Register(table, typeof(Second));

    var registered = PerspectiveDocumentModels.Registered.Where(m => m.Table == table).ToList();
    await Assert.That(registered.Count).IsEqualTo(1)
      .Because("two perspectives of one model share its table, and a table has one document type");
    await Assert.That(registered[0].Model).IsEqualTo(typeof(Second));
  }

  [Test]
  public async Task Register_EmptyTableOrNullModel_ThrowsAsync() {
    await Assert.That(() => PerspectiveDocumentModels.Register("", typeof(First))).Throws<ArgumentException>();
    await Assert.That(() => PerspectiveDocumentModels.Register("wh_per_x", null!)).Throws<ArgumentNullException>();
  }
}
