using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for <see cref="CallerInfo"/> and <see cref="ICallerInfo"/>.
/// </summary>
/// <tests>Whizbang.Core/Observability/ICallerInfo.cs</tests>
public class CallerInfoTests {
  [Test]
  public async Task CallerInfo_Constructor_SetsAllPropertiesAsync() {
    var info = new CallerInfo("MyMethod", "/path/to/file.cs", 42);

    await Assert.That(info.CallerMemberName).IsEqualTo("MyMethod");
    await Assert.That(info.CallerFilePath).IsEqualTo("/path/to/file.cs");
    await Assert.That(info.CallerLineNumber).IsEqualTo(42);
  }

  [Test]
  public async Task CallerInfo_ImplementsICallerInfoAsync() {
    var info = new CallerInfo("M", "F", 1);
    await Assert.That(info is ICallerInfo).IsTrue();
  }

  [Test]
  public async Task CallerInfo_RecordEquality_WorksCorrectlyAsync() {
    var info1 = new CallerInfo("Method", "File.cs", 10);
    var info2 = new CallerInfo("Method", "File.cs", 10);
    var info3 = new CallerInfo("Other", "File.cs", 10);

    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
  }

  [Test]
  public async Task CallerInfo_InterfaceProperties_MatchRecordPropertiesAsync() {
    CallerInfo info = new CallerInfo("HandleAsync", "/src/Handler.cs", 99);

    await Assert.That(info.CallerMemberName).IsEqualTo("HandleAsync");
    await Assert.That(info.CallerFilePath).IsEqualTo("/src/Handler.cs");
    await Assert.That(info.CallerLineNumber).IsEqualTo(99);
  }

  [Test]
  public async Task CallerInfo_ToString_ReturnsFormattedStringAsync() {
    var info = new CallerInfo("HandleOrderAsync", "/src/Orders/OrderHandler.cs", 42);

    await Assert.That(info.ToString()).IsEqualTo("HandleOrderAsync (/src/Orders/OrderHandler.cs:42)");
  }
}
