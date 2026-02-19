using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for MessageExtractor.ExtractMessagesWithRouting which extracts messages with their resolved routing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Internal/MessageExtractor.cs</code-under-test>
public class MessageExtractorRoutingTests {
  #region Null Handling

  [Test]
  public async Task ExtractMessagesWithRouting_WithNull_ReturnsEmptyAsync() {
    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(null).ToList();

    // Assert
    await Assert.That(results).IsEmpty();
  }

  #endregion

  #region Default Routing (Outbox - cross-service delivery)

  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedMessage_DefaultsToOutboxAsync() {
    // Arrange
    var evt = new TestEvent("Test");

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(evt).ToList();

    // Assert - Default is Outbox for cross-service delivery per routed cascade design
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Message).IsEqualTo(evt);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedArray_AllDefaultToOutboxAsync() {
    // Arrange
    IEvent[] events = [new TestEvent("A"), new TestEvent("B")];

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(events).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedTuple_AllDefaultToOutboxAsync() {
    // Arrange
    var tuple = (new TestEvent("A"), new TestEvent("B"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  #endregion

  #region Individual Wrapper

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualWrapper_UsesWrapperModeAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualWrapperOutbox_UsesOutboxAsync() {
    // Arrange
    var routed = Route.Outbox(new TestEvent("Test"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualWrapperBoth_UsesBothAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region Collection Wrapper

  [Test]
  public async Task ExtractMessagesWithRouting_CollectionWrapper_AppliesToAllItemsAsync() {
    // Arrange
    var routed = Route.Local(new IEvent[] { new TestEvent("A"), new TestEvent("B") });

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_TupleWrapper_AppliesToAllItemsAsync() {
    // Arrange
    var routed = Route.Outbox((new TestEvent("A"), new TestEvent("B")));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  #endregion

  #region Individual Overrides Collection

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualInsideCollection_IndividualWinsAsync() {
    // Arrange - Collection wrapper (Local) with individual override (Outbox)
    var routed = Route.Local(new object[] {
      new TestEvent("A"),                        // Should be Local (from collection)
      Route.Outbox(new TestEvent("B"))           // Should be Outbox (individual override)
    });

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);   // Collection default
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);  // Individual override
  }

  [Test]
  public async Task ExtractMessagesWithRouting_MultipleIndividualOverrides_EachWinsAsync() {
    // Arrange
    var routed = Route.Local(new object[] {
      Route.Outbox(new TestEvent("A")),     // Outbox (individual)
      new TestEvent("B"),                   // Local (collection)
      Route.Both(new TestEvent("C"))        // Both (individual)
    });

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);  // Individual
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Local);   // Collection
    await Assert.That(results[2].Mode).IsEqualTo(DispatchMode.Both);    // Individual
  }

  #endregion

  #region Tuple with Mixed Routing

  [Test]
  public async Task ExtractMessagesWithRouting_TupleWithMixedRouting_EachGetsCorrectModeAsync() {
    // Arrange - Tuple with per-item routing
    var tuple = (
      Route.Local(new TestEvent("A")),
      Route.Outbox(new TestEvent("B")),
      Route.Both(new TestEvent("C"))
    );

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(results[2].Mode).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region Receptor Default

  [Test]
  public async Task ExtractMessagesWithRouting_WithReceptorDefault_OverridesWrappersAsync() {
    // Arrange - Wrapper says Outbox, receptor says Local
    var routed = Route.Outbox(new TestEvent("Test"));

    // Act - Pass receptor default
    var results = MessageExtractor.ExtractMessagesWithRouting(
      routed,
      receptorDefault: DispatchMode.Local).ToList();

    // Assert - Receptor wins over wrapper
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithReceptorDefault_OverridesCollectionWrapperAsync() {
    // Arrange - Collection wrapper says Local
    var routed = Route.Local(new IEvent[] { new TestEvent("A"), new TestEvent("B") });

    // Act - Receptor says Outbox
    var results = MessageExtractor.ExtractMessagesWithRouting(
      routed,
      receptorDefault: DispatchMode.Outbox).ToList();

    // Assert - All items get receptor default
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_ReceptorDefaultNone_UsesWrappersAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Act - No receptor default (null)
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert - Wrapper is used
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  #endregion

  #region Message Attribute (Highest Priority)

  [Test]
  public async Task ExtractMessagesWithRouting_MessageAttribute_OverridesAllAsync() {
    // Arrange - LocalRoutedEvent has [DefaultRouting(DispatchMode.Local)]
    var evt = new LocalRoutedEvent();
    var routed = Route.Outbox(evt);  // Wrapper says Outbox

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(
      routed,
      receptorDefault: DispatchMode.Both).ToList();  // Receptor says Both

    // Assert - Message attribute wins
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_MessageAttribute_OverridesReceptorAsync() {
    // Arrange
    var evt = new LocalRoutedEvent();

    // Act - Receptor says Outbox
    var results = MessageExtractor.ExtractMessagesWithRouting(
      evt,
      receptorDefault: DispatchMode.Outbox).ToList();

    // Assert - Message attribute wins
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_MixedAttributeAndNonAttribute_CorrectRoutingAsync() {
    // Arrange - Tuple with attributed and non-attributed events
    var tuple = (
      new LocalRoutedEvent(),   // Has [DefaultRouting(Local)]
      new TestEvent("Test")     // No attribute -> uses default (Outbox)
    );

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);   // From attribute
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);  // System default (Outbox)
  }

  [Test]
  public async Task ExtractMessagesWithRouting_AttributeInsideCollectionWrapper_AttributeWinsAsync() {
    // Arrange - Collection says Outbox, but message has Local attribute
    var routed = Route.Outbox(new IEvent[] {
      new LocalRoutedEvent(),     // Has [DefaultRouting(Local)] -> Local
      new TestEvent("Test")       // No attribute -> Outbox (from collection)
    });

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);   // Attribute wins
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);  // Collection default
  }

  #endregion

  #region Complex Nested Structures

  [Test]
  public async Task ExtractMessagesWithRouting_NestedArrayInTuple_CorrectRoutingAsync() {
    // Arrange
    var tuple = (
      Route.Local(new TestEvent("Single")),
      new IEvent[] { new TestEvent("Array1"), new TestEvent("Array2") }
    );

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Local);   // Individual wrapper
    await Assert.That(results[1].Mode).IsEqualTo(DispatchMode.Outbox);  // System default (Outbox)
    await Assert.That(results[2].Mode).IsEqualTo(DispatchMode.Outbox);  // System default (Outbox)
  }

  #endregion

  #region Non-Message Types

  [Test]
  public async Task ExtractMessagesWithRouting_WithNonMessage_IgnoresNonMessageAsync() {
    // Arrange
    var tuple = (
      new TestEvent("Event"),
      "not a message",
      42
    );

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert - Only the event is extracted
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchMode.Outbox);  // System default (Outbox)
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  [DefaultRouting(DispatchMode.Local)]
  private sealed record LocalRoutedEvent : IEvent;

  #endregion
}
