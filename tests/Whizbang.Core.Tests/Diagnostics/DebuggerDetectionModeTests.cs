using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="DebuggerDetectionMode"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Diagnostics/DebuggerDetectionMode.cs</tests>
public class DebuggerDetectionModeTests {
  [Test]
  public async Task DebuggerDetectionMode_Disabled_IsDefinedAsync() {
    var value = DebuggerDetectionMode.Disabled;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_DebuggerAttached_IsDefinedAsync() {
    var value = DebuggerDetectionMode.DebuggerAttached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_CpuTimeSampling_IsDefinedAsync() {
    var value = DebuggerDetectionMode.CpuTimeSampling;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_ExternalHook_IsDefinedAsync() {
    var value = DebuggerDetectionMode.ExternalHook;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_Auto_IsDefinedAsync() {
    var value = DebuggerDetectionMode.Auto;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_HasFiveValuesAsync() {
    var values = Enum.GetValues<DebuggerDetectionMode>();
    await Assert.That(values.Length).IsEqualTo(5);
  }

  [Test]
  public async Task DebuggerDetectionMode_Disabled_HasCorrectIntValueAsync() {
    var value = (int)DebuggerDetectionMode.Disabled;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task DebuggerDetectionMode_DebuggerAttached_HasCorrectIntValueAsync() {
    var value = (int)DebuggerDetectionMode.DebuggerAttached;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task DebuggerDetectionMode_CpuTimeSampling_HasCorrectIntValueAsync() {
    var value = (int)DebuggerDetectionMode.CpuTimeSampling;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task DebuggerDetectionMode_ExternalHook_HasCorrectIntValueAsync() {
    var value = (int)DebuggerDetectionMode.ExternalHook;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task DebuggerDetectionMode_Auto_HasCorrectIntValueAsync() {
    var value = (int)DebuggerDetectionMode.Auto;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task DebuggerDetectionMode_Disabled_IsDefaultAsync() {
    var value = default(DebuggerDetectionMode);
    await Assert.That(value).IsEqualTo(DebuggerDetectionMode.Disabled);
  }
}
