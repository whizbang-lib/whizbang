using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="WhizbangOptions"/> and <see cref="GuidOrderingSeverity"/>.
/// </summary>
public class WhizbangOptionsTests {
  // ==========================================================================
  // WhizbangOptions default values tests
  // ==========================================================================

  [Test]
  public async Task WhizbangOptions_DefaultValues_DisableGuidTrackingIsFalseAsync() {
    // Arrange & Act
    var options = new WhizbangOptions();

    // Assert
    await Assert.That(options.DisableGuidTracking).IsFalse();
  }

  [Test]
  public async Task WhizbangOptions_DefaultValues_GuidOrderingViolationSeverityIsWarningAsync() {
    // Arrange & Act
    var options = new WhizbangOptions();

    // Assert
    await Assert.That(options.GuidOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Warning);
  }

  [Test]
  public async Task WhizbangOptions_DefaultValues_AutoGenerateStreamIdsIsTrueAsync() {
    // Arrange & Act
    var options = new WhizbangOptions();

    // Assert
    await Assert.That(options.AutoGenerateStreamIds).IsTrue();
  }

  // ==========================================================================
  // WhizbangOptions property assignment tests
  // ==========================================================================

  [Test]
  public async Task WhizbangOptions_SetDisableGuidTracking_PersistsValueAsync() {
    // Arrange
    var options = new WhizbangOptions {
      // Act
      DisableGuidTracking = true
    };

    // Assert
    await Assert.That(options.DisableGuidTracking).IsTrue();
  }

  [Test]
  public async Task WhizbangOptions_SetGuidOrderingViolationSeverity_PersistsValueAsync() {
    // Arrange
    var options = new WhizbangOptions {
      // Act
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error
    };

    // Assert
    await Assert.That(options.GuidOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Error);
  }

  [Test]
  public async Task WhizbangOptions_SetAutoGenerateStreamIds_PersistsValueAsync() {
    // Arrange
    var options = new WhizbangOptions {
      // Act
      AutoGenerateStreamIds = false
    };

    // Assert
    await Assert.That(options.AutoGenerateStreamIds).IsFalse();
  }

  // ==========================================================================
  // GuidOrderingSeverity enum tests
  // ==========================================================================

  [Test]
  public async Task GuidOrderingSeverity_None_IsDefinedAsync() {
    var value = GuidOrderingSeverity.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task GuidOrderingSeverity_Info_IsDefinedAsync() {
    var value = GuidOrderingSeverity.Info;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task GuidOrderingSeverity_Warning_IsDefinedAsync() {
    var value = GuidOrderingSeverity.Warning;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task GuidOrderingSeverity_Error_IsDefinedAsync() {
    var value = GuidOrderingSeverity.Error;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task GuidOrderingSeverity_HasFourValuesAsync() {
    // Arrange
    var values = Enum.GetValues<GuidOrderingSeverity>();

    // Assert
    await Assert.That(values.Length).IsEqualTo(4);
  }

  // ==========================================================================
  // GuidOrderingSeverity comparison tests
  // ==========================================================================

  [Test]
  public async Task GuidOrderingSeverity_None_HasCorrectIntValueAsync() {
    // Arrange
    var noneValue = (int)GuidOrderingSeverity.None;

    // Assert
    await Assert.That(noneValue).IsEqualTo(0);
  }

  [Test]
  public async Task GuidOrderingSeverity_Info_HasCorrectIntValueAsync() {
    // Arrange
    var infoValue = (int)GuidOrderingSeverity.Info;

    // Assert
    await Assert.That(infoValue).IsEqualTo(1);
  }

  [Test]
  public async Task GuidOrderingSeverity_Warning_HasCorrectIntValueAsync() {
    // Arrange
    var warningValue = (int)GuidOrderingSeverity.Warning;

    // Assert
    await Assert.That(warningValue).IsEqualTo(2);
  }

  [Test]
  public async Task GuidOrderingSeverity_Error_HasCorrectIntValueAsync() {
    // Arrange
    var errorValue = (int)GuidOrderingSeverity.Error;

    // Assert
    await Assert.That(errorValue).IsEqualTo(3);
  }

  [Test]
  public async Task GuidOrderingSeverity_SeverityOrder_IncreasesCorrectlyAsync() {
    // Arrange
    var none = (int)GuidOrderingSeverity.None;
    var info = (int)GuidOrderingSeverity.Info;
    var warning = (int)GuidOrderingSeverity.Warning;
    var error = (int)GuidOrderingSeverity.Error;

    // Assert - each severity level should be greater than the previous
    await Assert.That(info).IsGreaterThan(none);
    await Assert.That(warning).IsGreaterThan(info);
    await Assert.That(error).IsGreaterThan(warning);
  }

  // ==========================================================================
  // WhizbangOptions object initializer tests
  // ==========================================================================

  [Test]
  public async Task WhizbangOptions_ObjectInitializer_SetsAllPropertiesAsync() {
    // Arrange & Act
    var options = new WhizbangOptions {
      DisableGuidTracking = true,
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error,
      AutoGenerateStreamIds = false
    };

    // Assert
    await Assert.That(options.DisableGuidTracking).IsTrue();
    await Assert.That(options.GuidOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Error);
    await Assert.That(options.AutoGenerateStreamIds).IsFalse();
  }
}
