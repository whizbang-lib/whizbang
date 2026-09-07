using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage-round tests for <see cref="EventStoreTransformer"/> targeting the branch where a
/// bare `.Events` member access is not itself chained into a recognized Marten event-store
/// method call, so it must not register as a Marten event-sourcing pattern.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/EventStoreTransformer.cs:89</tests>
public class EventStoreTransformerCoverageTests {

  // If a bare "session.Events" property read (not chained into .StartStream()/.Append()/etc.)
  // were mistaken for a Marten event-store pattern, the transformer would start rewriting files
  // that merely happen to have an unrelated ".Events" property somewhere -- corrupting code
  // that has nothing to do with Marten's event store.
  [Test]
  public async Task TransformAsync_BareEventsPropertyAccessNotChainedToMethod_LeavesSourceUnchangedAsync() {
    // Arrange
    var transformer = new EventStoreTransformer();
    const string sourceCode = """
      public class OrderService {
        public void Peek(dynamic session) {
          var events = session.Events;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode)
      .Because("a bare .Events access with no recognized method chained onto it is not a Marten "
             + "event-store pattern and must not trigger any rewrite");
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }
}
