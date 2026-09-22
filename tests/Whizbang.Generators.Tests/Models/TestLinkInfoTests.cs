using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests.Models;

/// <summary>
/// Tests for the <see cref="TestLinkInfo"/> value record and <see cref="TestLinkSource"/>.
/// Ensures the positional members land in the right slots and that value equality holds,
/// since the generator dedupes links by equality.
/// </summary>
public class TestLinkInfoTests {

  private static TestLinkInfo _sample(TestLinkSource source = TestLinkSource.Convention)
      => new(
          SourceFile: "src/Whizbang.Core/Dispatcher.cs",
          SourceLine: 42,
          SourceSymbol: "SendAsync",
          SourceType: "Whizbang.Core.Dispatcher",
          TestFile: "tests/Whizbang.Core.Tests/DispatcherTests.cs",
          TestLine: 17,
          TestMethod: "SendAsync_RoutesAsync",
          TestClass: "Whizbang.Core.Tests.DispatcherTests",
          LinkSource: source);

  [Test]
  public async Task Constructor_AssignsEveryPositionalMemberAsync() {
    var info = _sample();

    await Assert.That(info.SourceFile).IsEqualTo("src/Whizbang.Core/Dispatcher.cs");
    await Assert.That(info.SourceLine).IsEqualTo(42);
    await Assert.That(info.SourceSymbol).IsEqualTo("SendAsync");
    await Assert.That(info.SourceType).IsEqualTo("Whizbang.Core.Dispatcher");
    await Assert.That(info.TestFile).IsEqualTo("tests/Whizbang.Core.Tests/DispatcherTests.cs");
    await Assert.That(info.TestLine).IsEqualTo(17);
    await Assert.That(info.TestMethod).IsEqualTo("SendAsync_RoutesAsync");
    await Assert.That(info.TestClass).IsEqualTo("Whizbang.Core.Tests.DispatcherTests");
    await Assert.That(info.LinkSource).IsEqualTo(TestLinkSource.Convention);
  }

  [Test]
  public async Task TestLinkInfo_WithSameValues_AreEqualAsync() {
    await Assert.That(_sample()).IsEqualTo(_sample());
  }

  [Test]
  public async Task TestLinkInfo_WithSameValues_ShareAHashCodeAsync() {
    await Assert.That(_sample().GetHashCode()).IsEqualTo(_sample().GetHashCode());
  }

  [Test]
  public async Task TestLinkInfo_DifferingOnlyByLinkSource_AreNotEqualAsync() {
    await Assert.That(_sample(TestLinkSource.Convention))
        .IsNotEqualTo(_sample(TestLinkSource.XmlTag));
  }

  [Test]
  public async Task TestLinkInfo_WithChangedMember_IsNotEqualAsync() {
    // A `with` expression is not usable here: Whizbang.Generators ILRepacks its own
    // IsExternalInit polyfill, so the init accessors are not consumable from this assembly.
    var mutated = new TestLinkInfo(
        SourceFile: "src/Whizbang.Core/Dispatcher.cs",
        SourceLine: 43,
        SourceSymbol: "SendAsync",
        SourceType: "Whizbang.Core.Dispatcher",
        TestFile: "tests/Whizbang.Core.Tests/DispatcherTests.cs",
        TestLine: 17,
        TestMethod: "SendAsync_RoutesAsync",
        TestClass: "Whizbang.Core.Tests.DispatcherTests",
        LinkSource: TestLinkSource.Convention);

    await Assert.That(mutated).IsNotEqualTo(_sample());
    await Assert.That(mutated.SourceLine).IsEqualTo(43);
  }

  [Test]
  [Arguments(TestLinkSource.Convention)]
  [Arguments(TestLinkSource.SemanticAnalysis)]
  [Arguments(TestLinkSource.XmlTag)]
  public async Task TestLinkSource_RoundTripsThroughTheRecordAsync(TestLinkSource source) {
    await Assert.That(_sample(source).LinkSource).IsEqualTo(source);
  }

  [Test]
  public async Task TestLinkSource_HasExactlyThreeDistinctValuesAsync() {
    var values = Enum.GetValues<TestLinkSource>();

    await Assert.That(values.Length).IsEqualTo(3);
    await Assert.That(values.Distinct().Count()).IsEqualTo(values.Length);
  }
}
