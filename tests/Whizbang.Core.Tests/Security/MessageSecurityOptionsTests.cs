using TUnit.Core;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="MessageSecurityOptions"/>.
/// Verifies default values and configuration behavior.
/// </summary>
[Category("Security")]
public class MessageSecurityOptionsTests {
  // === Default Value Tests ===

  [Test]
  public async Task AllowAnonymous_Default_IsFalseAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.AllowAnonymous).IsFalse()
      .Because("Default should follow least privilege principle");
  }

  [Test]
  public async Task EnableAuditLogging_Default_IsTrueAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.EnableAuditLogging).IsTrue()
      .Because("Audit logging should be enabled by default");
  }

  [Test]
  public async Task ValidateCredentials_Default_IsTrueAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.ValidateCredentials).IsTrue()
      .Because("Credentials should be validated by default");
  }

  [Test]
  public async Task Timeout_Default_IsFiveSecondsAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(5))
      .Because("Default timeout should be 5 seconds");
  }

  [Test]
  public async Task PropagateToOutgoingMessages_Default_IsTrueAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.PropagateToOutgoingMessages).IsTrue()
      .Because("Security context should propagate to outgoing messages by default");
  }

  [Test]
  public async Task ExemptMessageTypes_Default_IsEmptyAsync() {
    // Arrange
    var options = new MessageSecurityOptions();

    // Assert
    await Assert.That(options.ExemptMessageTypes).IsNotNull();
    await Assert.That(options.ExemptMessageTypes.Count).IsEqualTo(0)
      .Because("No message types should be exempt by default");
  }

  // === Configuration Tests ===

  [Test]
  public async Task AllowAnonymous_CanBeSetToTrueAsync() {
    // Arrange
    var options = new MessageSecurityOptions { AllowAnonymous = true };

    // Assert
    await Assert.That(options.AllowAnonymous).IsTrue();
  }

  [Test]
  public async Task ExemptMessageTypes_CanAddTypesAsync() {
    // Arrange
    var options = new MessageSecurityOptions();
    options.ExemptMessageTypes.Add(typeof(TestMessage));
    options.ExemptMessageTypes.Add(typeof(AnotherTestMessage));

    // Assert
    await Assert.That(options.ExemptMessageTypes.Count).IsEqualTo(2);
    await Assert.That(options.ExemptMessageTypes.Contains(typeof(TestMessage))).IsTrue();
    await Assert.That(options.ExemptMessageTypes.Contains(typeof(AnotherTestMessage))).IsTrue();
  }

  [Test]
  public async Task ExemptMessageTypes_NoDuplicatesAsync() {
    // Arrange
    var options = new MessageSecurityOptions();
    options.ExemptMessageTypes.Add(typeof(TestMessage));
    options.ExemptMessageTypes.Add(typeof(TestMessage)); // Duplicate

    // Assert - HashSet ignores duplicates
    await Assert.That(options.ExemptMessageTypes.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Timeout_CanBeCustomizedAsync() {
    // Arrange
    var customTimeout = TimeSpan.FromSeconds(30);
    var options = new MessageSecurityOptions { Timeout = customTimeout };

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(customTimeout);
  }

  [Test]
  public async Task EnableAuditLogging_CanBeDisabledAsync() {
    // Arrange
    var options = new MessageSecurityOptions { EnableAuditLogging = false };

    // Assert
    await Assert.That(options.EnableAuditLogging).IsFalse();
  }

  [Test]
  public async Task ValidateCredentials_CanBeDisabledAsync() {
    // Arrange
    var options = new MessageSecurityOptions { ValidateCredentials = false };

    // Assert
    await Assert.That(options.ValidateCredentials).IsFalse();
  }

  [Test]
  public async Task PropagateToOutgoingMessages_CanBeDisabledAsync() {
    // Arrange
    var options = new MessageSecurityOptions { PropagateToOutgoingMessages = false };

    // Assert
    await Assert.That(options.PropagateToOutgoingMessages).IsFalse();
  }

  // === Test Message Types ===

  private sealed record TestMessage(string Data);
  private sealed record AnotherTestMessage(int Value);
}
