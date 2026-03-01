using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="TraceVerbosity"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Tracing/TraceVerbosity.cs</tests>
public class TraceVerbosityTests {
  [Test]
  public async Task TraceVerbosity_Off_IsDefinedAsync() {
    var value = TraceVerbosity.Off;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_Minimal_IsDefinedAsync() {
    var value = TraceVerbosity.Minimal;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_Normal_IsDefinedAsync() {
    var value = TraceVerbosity.Normal;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_Verbose_IsDefinedAsync() {
    var value = TraceVerbosity.Verbose;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_Debug_IsDefinedAsync() {
    var value = TraceVerbosity.Debug;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_HasFiveValuesAsync() {
    var values = Enum.GetValues<TraceVerbosity>();
    await Assert.That(values.Length).IsEqualTo(5);
  }

  [Test]
  public async Task TraceVerbosity_Off_HasCorrectIntValueAsync() {
    var value = (int)TraceVerbosity.Off;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task TraceVerbosity_Minimal_HasCorrectIntValueAsync() {
    var value = (int)TraceVerbosity.Minimal;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task TraceVerbosity_Normal_HasCorrectIntValueAsync() {
    var value = (int)TraceVerbosity.Normal;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task TraceVerbosity_Verbose_HasCorrectIntValueAsync() {
    var value = (int)TraceVerbosity.Verbose;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task TraceVerbosity_Debug_HasCorrectIntValueAsync() {
    var value = (int)TraceVerbosity.Debug;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task TraceVerbosity_Off_IsDefaultAsync() {
    var value = default(TraceVerbosity);
    await Assert.That(value).IsEqualTo(TraceVerbosity.Off);
  }

  [Test]
  public async Task TraceVerbosity_VerbosityHierarchy_IsCorrectAsync() {
    // Verify verbosity levels increase in a hierarchical order
    var off = (int)TraceVerbosity.Off;
    var minimal = (int)TraceVerbosity.Minimal;
    var normal = (int)TraceVerbosity.Normal;
    var verbose = (int)TraceVerbosity.Verbose;
    var debug = (int)TraceVerbosity.Debug;

    await Assert.That(minimal).IsGreaterThan(off);
    await Assert.That(normal).IsGreaterThan(minimal);
    await Assert.That(verbose).IsGreaterThan(normal);
    await Assert.That(debug).IsGreaterThan(verbose);
  }
}
