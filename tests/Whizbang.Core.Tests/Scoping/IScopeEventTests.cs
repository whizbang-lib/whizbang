using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for the <see cref="IScopeEvent"/> interface contract.
/// </summary>
/// <tests>src/Whizbang.Core/IScopeEvent.cs</tests>
public class IScopeEventTests {
  [Test]
  public async Task IScopeEvent_ImplementsIEventAsync() {
    // Assert - IScopeEvent inherits from IEvent
    await Assert.That(typeof(IScopeEvent).GetInterfaces()).Contains(typeof(IEvent));
  }

  [Test]
  public async Task IScopeEvent_ImplementsIMessageAsync() {
    // Assert - IScopeEvent inherits from IMessage (via IEvent)
    await Assert.That(typeof(IEvent).GetInterfaces()).Contains(typeof(IMessage));
  }

  [Test]
  public async Task IScopeEvent_ScopeProperty_ReturnsPerspectiveScopeAsync() {
    // Arrange
    IScopeEvent scopeEvent = new TestScopeEvent {
      Scope = new PerspectiveScope { TenantId = "tenant-new", UserId = "user-new" }
    };

    // Act & Assert
    await Assert.That(scopeEvent.Scope).IsNotNull();
    await Assert.That(scopeEvent.Scope.TenantId).IsEqualTo("tenant-new");
    await Assert.That(scopeEvent.Scope.UserId).IsEqualTo("user-new");
  }

  [Test]
  public async Task IScopeEvent_CanBeCheckedWithPatternMatchingAsync() {
    // Arrange - an event that is also a scope event
    IEvent @event = new TestScopeEvent {
      Scope = new PerspectiveScope { TenantId = "tenant-match" }
    };

    // Act
    var isScopeEvent = @event is IScopeEvent;
    var scope = (@event as IScopeEvent)?.Scope;

    // Assert
    await Assert.That(isScopeEvent).IsTrue();
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("tenant-match");
  }

  [Test]
  public async Task RegularEvent_IsNotIScopeEventAsync() {
    // Arrange
    IEvent @event = new TestRegularEvent();

    // Act & Assert
    await Assert.That(@event is IScopeEvent).IsFalse();
  }

  // === Test Models ===

  private sealed record TestScopeEvent : IScopeEvent {
    public required PerspectiveScope Scope { get; init; }
  }

  private sealed record TestRegularEvent : IEvent;
}
