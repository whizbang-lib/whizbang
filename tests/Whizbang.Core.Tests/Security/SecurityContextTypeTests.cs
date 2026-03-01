using TUnit.Core;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="SecurityContextType"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Security/SecurityContextType.cs</tests>
public class SecurityContextTypeTests {
  [Test]
  public async Task SecurityContextType_User_IsDefinedAsync() {
    var value = SecurityContextType.User;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SecurityContextType_System_IsDefinedAsync() {
    var value = SecurityContextType.System;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SecurityContextType_Impersonated_IsDefinedAsync() {
    var value = SecurityContextType.Impersonated;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SecurityContextType_ServiceAccount_IsDefinedAsync() {
    var value = SecurityContextType.ServiceAccount;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SecurityContextType_HasFourValuesAsync() {
    var values = Enum.GetValues<SecurityContextType>();
    await Assert.That(values.Length).IsEqualTo(4);
  }

  [Test]
  public async Task SecurityContextType_User_HasCorrectIntValueAsync() {
    var value = (int)SecurityContextType.User;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task SecurityContextType_System_HasCorrectIntValueAsync() {
    var value = (int)SecurityContextType.System;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task SecurityContextType_Impersonated_HasCorrectIntValueAsync() {
    var value = (int)SecurityContextType.Impersonated;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task SecurityContextType_ServiceAccount_HasCorrectIntValueAsync() {
    var value = (int)SecurityContextType.ServiceAccount;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task SecurityContextType_User_IsDefaultAsync() {
    var value = default(SecurityContextType);
    await Assert.That(value).IsEqualTo(SecurityContextType.User);
  }
}
