using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="PerspectiveRewindOptions"/> configuration.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/PerspectiveRewindOptions.cs</tests>
public class PerspectiveRewindOptionsTests {

  [Test]
  public async Task Defaults_AllFieldsHaveExpectedValuesAsync() {
    var options = new PerspectiveRewindOptions();

    await Assert.That(options.Enabled).IsTrue()
      .Because("Rewind should be enabled by default");
    await Assert.That(options.StartupScanEnabled).IsTrue()
      .Because("Startup scan should be enabled by default");
    await Assert.That(options.StartupRewindMode).IsEqualTo(RewindStartupMode.Blocking)
      .Because("Startup rewinds should block by default to ensure data integrity");
    await Assert.That(options.MaxConcurrentRewinds).IsEqualTo(3)
      .Because("Default concurrency should be 3");
  }

  [Test]
  public async Task Properties_CanBeSetAsync() {
    var options = new PerspectiveRewindOptions {
      Enabled = false,
      StartupScanEnabled = false,
      StartupRewindMode = RewindStartupMode.Background,
      MaxConcurrentRewinds = 10
    };

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.StartupScanEnabled).IsFalse();
    await Assert.That(options.StartupRewindMode).IsEqualTo(RewindStartupMode.Background);
    await Assert.That(options.MaxConcurrentRewinds).IsEqualTo(10);
  }

  [Test]
  public async Task RewindStartupMode_HasExpectedValuesAsync() {
    var values = Enum.GetValues<RewindStartupMode>();
    await Assert.That(values).Count().IsEqualTo(2)
      .Because("Should have Blocking and Background modes");

    var blocking = (int)RewindStartupMode.Blocking;
    var background = (int)RewindStartupMode.Background;
    await Assert.That(blocking).IsEqualTo(0);
    await Assert.That(background).IsEqualTo(1);
  }
}
