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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedArray_AllDefaultToOutboxAsync() {
    // Arrange
    IEvent[] events = [new TestEvent("A"), new TestEvent("B")];

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(events).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedTuple_AllDefaultToOutboxAsync() {
    // Arrange
    var tuple = (new TestEvent("A"), new TestEvent("B"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualWrapperOutbox_UsesOutboxAsync() {
    // Arrange
    var routed = Route.Outbox(new TestEvent("Test"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_IndividualWrapperBoth_UsesBothAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Both);
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_TupleWrapper_AppliesToAllItemsAsync() {
    // Arrange
    var routed = Route.Outbox((new TestEvent("A"), new TestEvent("B")));

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);   // Collection default
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);  // Individual override
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);  // Individual
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Local);   // Collection
    await Assert.That(results[2].Mode).IsEqualTo(DispatchModes.Both);    // Individual
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(results[2].Mode).IsEqualTo(DispatchModes.Both);
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
      receptorDefault: DispatchModes.Local).ToList();

    // Assert - Receptor wins over wrapper
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithReceptorDefault_OverridesCollectionWrapperAsync() {
    // Arrange - Collection wrapper says Local
    var routed = Route.Local(new IEvent[] { new TestEvent("A"), new TestEvent("B") });

    // Act - Receptor says Outbox
    var results = MessageExtractor.ExtractMessagesWithRouting(
      routed,
      receptorDefault: DispatchModes.Outbox).ToList();

    // Assert - All items get receptor default
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_ReceptorDefaultNone_UsesWrappersAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Act - No receptor default (null)
    var results = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert - Wrapper is used
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
  }

  #endregion

  #region Message Attribute (Highest Priority)

  [Test]
  public async Task ExtractMessagesWithRouting_MessageAttribute_OverridesAllAsync() {
    // Arrange - LocalRoutedEvent has [DefaultRouting(DispatchModes.Local)]
    var evt = new LocalRoutedEvent();
    var routed = Route.Outbox(evt);  // Wrapper says Outbox

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(
      routed,
      receptorDefault: DispatchModes.Both).ToList();  // Receptor says Both

    // Assert - Message attribute wins
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_MessageAttribute_OverridesReceptorAsync() {
    // Arrange
    var evt = new LocalRoutedEvent();

    // Act - Receptor says Outbox
    var results = MessageExtractor.ExtractMessagesWithRouting(
      evt,
      receptorDefault: DispatchModes.Outbox).ToList();

    // Assert - Message attribute wins
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);   // From attribute
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);  // System default (Outbox)
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);   // Attribute wins
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);  // Collection default
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Local);   // Individual wrapper
    await Assert.That(results[1].Mode).IsEqualTo(DispatchModes.Outbox);  // System default (Outbox)
    await Assert.That(results[2].Mode).IsEqualTo(DispatchModes.Outbox);  // System default (Outbox)
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
    await Assert.That(results[0].Mode).IsEqualTo(DispatchModes.Outbox);  // System default (Outbox)
  }

  #endregion

  #region Route.None (Discriminated Union Support)

  [Test]
  public async Task ExtractMessagesWithRouting_RouteNone_IsSkippedAsync() {
    // Arrange - Route.None() should not produce any messages
    var routeNone = Route.None();

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(routeNone).ToList();

    // Assert
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task ExtractMessagesWithRouting_TupleWithRouteNone_SkipsNoneAsync() {
    // Arrange - Discriminated union: success path
    var successEvent = new TestEvent("Success");
    var tuple = (success: (object)successEvent, failure: Route.None());

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert - Only the success event, Route.None() is skipped
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Message).IsEqualTo(successEvent);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_TupleWithRouteNone_FailurePathAsync() {
    // Arrange - Discriminated union: failure path
    var failureEvent = new TestEvent("Failure");
    var tuple = (success: Route.None(), failure: (object)failureEvent);

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert - Only the failure event
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Message).IsEqualTo(failureEvent);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_AllRouteNone_ReturnsEmptyAsync() {
    // Arrange - Tuple with only Route.None() values
    var tuple = (Route.None(), Route.None());

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task ExtractMessagesWithRouting_ThreeWayUnionWithRouteNone_ExtractsOnlyValueAsync() {
    // Arrange - Three-way discriminated union
    var validationError = new TestEvent("ValidationFailed");
    var tuple = (
      success: Route.None(),
      validationError: (object)validationError,
      systemError: Route.None()
    );

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert - Only the validation error event
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Message).IsEqualTo(validationError);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_ArrayWithRouteNone_SkipsNoneAsync() {
    // Arrange - Array with Route.None() values
    var evt1 = new TestEvent("A");
    var evt2 = new TestEvent("B");
    var array = new object[] { evt1, Route.None(), evt2, Route.None() };

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(array).ToList();

    // Assert - Only the events
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Message).IsEqualTo(evt1);
    await Assert.That(results[1].Message).IsEqualTo(evt2);
  }

  [Test]
  public async Task ExtractMessagesWithRouting_MixedNullAndRouteNone_BothSkippedAsync() {
    // Arrange - Mix of null and Route.None()
    var evt = new TestEvent("Event");
    TestEvent? nullEvent = null;
    var tuple = (evt1: (object?)evt, evt2: (object?)nullEvent, evt3: Route.None());

    // Act
    var results = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert - Only the non-null, non-None event
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Message).IsEqualTo(evt);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  [DefaultRouting(DispatchModes.Local)]
  private sealed record LocalRoutedEvent : IEvent;

  #endregion
}
