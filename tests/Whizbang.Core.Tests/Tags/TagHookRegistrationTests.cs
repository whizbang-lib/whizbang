using System;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TagHookRegistration"/>.
/// Validates hook registration records and priority handling.
/// </summary>
/// <tests>Whizbang.Core/Tags/TagHookRegistration.cs</tests>
[Category("Core")]
[Category("Tags")]
public class TagHookRegistrationTests {

  [Test]
  public async Task TagHookRegistration_DefaultPriority_IsMinusOneHundredAsync() {
    // Assert
    await Assert.That(TagHookRegistration.DefaultPriority).IsEqualTo(-100);
  }

  [Test]
  public async Task TagHookRegistration_Constructor_SetsAttributeTypeAsync() {
    // Act
    var registration = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook)
    );

    // Assert
    await Assert.That(registration.AttributeType).IsEqualTo(typeof(NotificationTagAttribute));
  }

  [Test]
  public async Task TagHookRegistration_Constructor_SetsHookTypeAsync() {
    // Act
    var registration = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook)
    );

    // Assert
    await Assert.That(registration.HookType).IsEqualTo(typeof(TestNotificationHook));
  }

  [Test]
  public async Task TagHookRegistration_Constructor_DefaultsPriorityToMinusOneHundredAsync() {
    // Act
    var registration = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook)
    );

    // Assert
    await Assert.That(registration.Priority).IsEqualTo(-100);
  }

  [Test]
  public async Task TagHookRegistration_Constructor_AcceptsCustomPriorityAsync() {
    // Act
    var registration = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: 50
    );

    // Assert
    await Assert.That(registration.Priority).IsEqualTo(50);
  }

  [Test]
  public async Task TagHookRegistration_Constructor_AcceptsNegativePriorityAsync() {
    // Act
    var registration = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: -10
    );

    // Assert
    await Assert.That(registration.Priority).IsEqualTo(-10);
  }

  [Test]
  public async Task TagHookRegistration_Equality_WorksForSameValuesAsync() {
    // Arrange
    var registration1 = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: 10
    );
    var registration2 = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: 10
    );

    // Assert
    await Assert.That(registration1).IsEqualTo(registration2);
  }

  [Test]
  public async Task TagHookRegistration_Inequality_DifferentPriorityAsync() {
    // Arrange
    var registration1 = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: 10
    );
    var registration2 = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook),
      Priority: 20
    );

    // Assert
    await Assert.That(registration1).IsNotEqualTo(registration2);
  }

  [Test]
  public async Task TagHookRegistration_Inequality_DifferentAttributeTypeAsync() {
    // Arrange
    var registration1 = new TagHookRegistration(
      typeof(NotificationTagAttribute),
      typeof(TestNotificationHook)
    );
    var registration2 = new TagHookRegistration(
      typeof(TelemetryTagAttribute),
      typeof(TestNotificationHook)
    );

    // Assert
    await Assert.That(registration1).IsNotEqualTo(registration2);
  }

  [Test]
  public async Task TagHookRegistration_CanSortByPriorityAsync() {
    // Arrange
    var registrations = new[] {
      new TagHookRegistration(typeof(NotificationTagAttribute), typeof(TestNotificationHook), Priority: 500),
      new TagHookRegistration(typeof(NotificationTagAttribute), typeof(TestNotificationHook), Priority: -100),
      new TagHookRegistration(typeof(NotificationTagAttribute), typeof(TestNotificationHook), Priority: 30),
      new TagHookRegistration(typeof(NotificationTagAttribute), typeof(TestNotificationHook), Priority: -10)
    };

    // Act
    var sorted = registrations.OrderBy(r => r.Priority).ToArray();

    // Assert
    await Assert.That(sorted[0].Priority).IsEqualTo(-100);
    await Assert.That(sorted[1].Priority).IsEqualTo(-10);
    await Assert.That(sorted[2].Priority).IsEqualTo(30);
    await Assert.That(sorted[3].Priority).IsEqualTo(500);
  }

  // Test helper hook implementation
  private sealed class TestNotificationHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken ct) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }
}
