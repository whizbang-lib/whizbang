using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceVerbosity enum which controls tracing detail levels.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceVerbosity.cs</code-under-test>
public class TraceVerbosityTests {
  private static readonly int[] _expectedSequentialValues = [0, 1, 2, 3, 4];
  #region Enum Value Tests

  [Test]
  public async Task Off_HasValue_ZeroAsync() {
    // Arrange
    var verbosity = TraceVerbosity.Off;

    // Assert
    await Assert.That((int)verbosity).IsEqualTo(0);
  }

  [Test]
  public async Task Minimal_HasValue_OneAsync() {
    // Arrange
    var verbosity = TraceVerbosity.Minimal;

    // Assert - Minimal is for errors and explicit traces only
    await Assert.That((int)verbosity).IsEqualTo(1);
  }

  [Test]
  public async Task Normal_HasValue_TwoAsync() {
    // Arrange
    var verbosity = TraceVerbosity.Normal;

    // Assert - Normal adds lifecycle stage transitions
    await Assert.That((int)verbosity).IsEqualTo(2);
  }

  [Test]
  public async Task Verbose_HasValue_ThreeAsync() {
    // Arrange
    var verbosity = TraceVerbosity.Verbose;

    // Assert - Verbose adds handler discovery and outbox/inbox ops
    await Assert.That((int)verbosity).IsEqualTo(3);
  }

  [Test]
  public async Task Debug_HasValue_FourAsync() {
    // Arrange
    var verbosity = TraceVerbosity.Debug;

    // Assert - Debug is maximum detail including payloads
    await Assert.That((int)verbosity).IsEqualTo(4);
  }

  #endregion

  #region Ordering Tests (Hierarchical)

  [Test]
  public async Task Off_IsLessThan_MinimalAsync() {
    // Arrange
    var off = TraceVerbosity.Off;
    var minimal = TraceVerbosity.Minimal;

    // Assert - Off < Minimal (hierarchical ordering)
    await Assert.That(off < minimal).IsTrue();
  }

  [Test]
  public async Task Minimal_IsLessThan_NormalAsync() {
    // Arrange
    var minimal = TraceVerbosity.Minimal;
    var normal = TraceVerbosity.Normal;

    // Assert - Minimal < Normal (hierarchical ordering)
    await Assert.That(minimal < normal).IsTrue();
  }

  [Test]
  public async Task Normal_IsLessThan_VerboseAsync() {
    // Arrange
    var normal = TraceVerbosity.Normal;
    var verbose = TraceVerbosity.Verbose;

    // Assert - Normal < Verbose (hierarchical ordering)
    await Assert.That(normal < verbose).IsTrue();
  }

  [Test]
  public async Task Verbose_IsLessThan_DebugAsync() {
    // Arrange
    var verbose = TraceVerbosity.Verbose;
    var debug = TraceVerbosity.Debug;

    // Assert - Verbose < Debug (hierarchical ordering)
    await Assert.That(verbose < debug).IsTrue();
  }

  [Test]
  public async Task Debug_IsGreatestValueAsync() {
    // Arrange
    var allValues = Enum.GetValues<TraceVerbosity>();

    // Assert - Debug is the maximum value
    await Assert.That(allValues.Max()).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task Off_IsSmallestValueAsync() {
    // Arrange
    var allValues = Enum.GetValues<TraceVerbosity>();

    // Assert - Off is the minimum value
    await Assert.That(allValues.Min()).IsEqualTo(TraceVerbosity.Off);
  }

  #endregion

  #region Level Inclusion Tests (Higher includes Lower)

  [Test]
  public async Task Normal_IncludesMinimal_WhenComparedAsync() {
    // Arrange
    var currentVerbosity = TraceVerbosity.Normal;
    var requiredVerbosity = TraceVerbosity.Minimal;

    // Assert - Normal includes Minimal (>=)
    await Assert.That(currentVerbosity >= requiredVerbosity).IsTrue();
  }

  [Test]
  public async Task Verbose_IncludesNormal_WhenComparedAsync() {
    // Arrange
    var currentVerbosity = TraceVerbosity.Verbose;
    var requiredVerbosity = TraceVerbosity.Normal;

    // Assert - Verbose includes Normal (>=)
    await Assert.That(currentVerbosity >= requiredVerbosity).IsTrue();
  }

  [Test]
  public async Task Debug_IncludesAll_WhenComparedAsync() {
    // Arrange
    var currentVerbosity = TraceVerbosity.Debug;
    var off = TraceVerbosity.Off;
    var minimal = TraceVerbosity.Minimal;
    var normal = TraceVerbosity.Normal;
    var verbose = TraceVerbosity.Verbose;
    var debug = TraceVerbosity.Debug;

    // Assert - Debug includes all levels
    await Assert.That(currentVerbosity >= off).IsTrue();
    await Assert.That(currentVerbosity >= minimal).IsTrue();
    await Assert.That(currentVerbosity >= normal).IsTrue();
    await Assert.That(currentVerbosity >= verbose).IsTrue();
    await Assert.That(currentVerbosity >= debug).IsTrue();
  }

  [Test]
  public async Task Minimal_DoesNotIncludeNormal_WhenComparedAsync() {
    // Arrange
    var currentVerbosity = TraceVerbosity.Minimal;
    var requiredVerbosity = TraceVerbosity.Normal;

    // Assert - Minimal does not include Normal
    await Assert.That(currentVerbosity >= requiredVerbosity).IsFalse();
  }

  [Test]
  public async Task Off_DoesNotIncludeAnything_ExceptItselfAsync() {
    // Arrange
    var currentVerbosity = TraceVerbosity.Off;
    var off = TraceVerbosity.Off;
    var minimal = TraceVerbosity.Minimal;
    var normal = TraceVerbosity.Normal;
    var verbose = TraceVerbosity.Verbose;
    var debug = TraceVerbosity.Debug;

    // Assert - Off only includes itself
    await Assert.That(currentVerbosity >= off).IsTrue();
    await Assert.That(currentVerbosity >= minimal).IsFalse();
    await Assert.That(currentVerbosity >= normal).IsFalse();
    await Assert.That(currentVerbosity >= verbose).IsFalse();
    await Assert.That(currentVerbosity >= debug).IsFalse();
  }

  #endregion

  #region Enum Definition Tests

  [Test]
  public async Task TraceVerbosity_HasExactlyFiveValuesAsync() {
    // Arrange
    var allValues = Enum.GetValues<TraceVerbosity>();

    // Assert - Exactly 5 verbosity levels
    await Assert.That(allValues.Length).IsEqualTo(5);
  }

  [Test]
  public async Task TraceVerbosity_ValuesAreSequentialAsync() {
    // Arrange
    var allValues = Enum.GetValues<TraceVerbosity>().Cast<int>().Order().ToArray();

    // Assert - Values are 0, 1, 2, 3, 4 (sequential)
    await Assert.That(allValues).IsEquivalentTo(_expectedSequentialValues);
  }

  [Test]
  public async Task TraceVerbosity_CanBeParsedFromStringAsync() {
    // Assert - All values can be parsed from their string names
    await Assert.That(Enum.TryParse<TraceVerbosity>("Off", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceVerbosity>("Minimal", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceVerbosity>("Normal", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceVerbosity>("Verbose", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceVerbosity>("Debug", out _)).IsTrue();
  }

  [Test]
  public async Task TraceVerbosity_CanBeParsedCaseInsensitiveAsync() {
    // Assert - Values can be parsed case-insensitively (for config binding)
    await Assert.That(Enum.TryParse<TraceVerbosity>("off", true, out var off)).IsTrue();
    await Assert.That(off).IsEqualTo(TraceVerbosity.Off);

    await Assert.That(Enum.TryParse<TraceVerbosity>("MINIMAL", true, out var minimal)).IsTrue();
    await Assert.That(minimal).IsEqualTo(TraceVerbosity.Minimal);

    await Assert.That(Enum.TryParse<TraceVerbosity>("debug", true, out var debug)).IsTrue();
    await Assert.That(debug).IsEqualTo(TraceVerbosity.Debug);
  }

  #endregion
}
