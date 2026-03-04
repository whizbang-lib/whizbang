using TUnit.Core;
using Whizbang.Core.Audit;

namespace Whizbang.Core.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditLevel"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Audit/AuditLevel.cs</tests>
public class AuditLevelTests {
  [Test]
  public async Task AuditLevel_Info_IsDefinedAsync() {
    var value = AuditLevel.Info;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task AuditLevel_Warning_IsDefinedAsync() {
    var value = AuditLevel.Warning;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task AuditLevel_Critical_IsDefinedAsync() {
    var value = AuditLevel.Critical;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task AuditLevel_HasThreeValuesAsync() {
    var values = Enum.GetValues<AuditLevel>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task AuditLevel_Info_HasCorrectIntValueAsync() {
    var value = (int)AuditLevel.Info;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task AuditLevel_Warning_HasCorrectIntValueAsync() {
    var value = (int)AuditLevel.Warning;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task AuditLevel_Critical_HasCorrectIntValueAsync() {
    var value = (int)AuditLevel.Critical;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task AuditLevel_Info_IsDefaultAsync() {
    var value = default(AuditLevel);
    await Assert.That(value).IsEqualTo(AuditLevel.Info);
  }

  [Test]
  public async Task AuditLevel_SeverityOrder_IsCorrectAsync() {
    // Verify severity levels increase: Info < Warning < Critical
    var info = (int)AuditLevel.Info;
    var warning = (int)AuditLevel.Warning;
    var critical = (int)AuditLevel.Critical;

    await Assert.That(warning).IsGreaterThan(info);
    await Assert.That(critical).IsGreaterThan(warning);
  }
}
