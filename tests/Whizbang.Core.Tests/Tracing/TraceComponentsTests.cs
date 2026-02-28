using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceComponents flags enum which controls which system components emit trace output.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceComponents.cs</code-under-test>
public class TraceComponentsTests {
  #region Base Flag Values (Powers of 2)

  [Test]
  public async Task None_HasValue_ZeroAsync() {
    // Arrange
    var components = TraceComponents.None;

    // Assert
    await Assert.That((int)components).IsEqualTo(0);
  }

  [Test]
  public async Task Http_HasValue_OneAsync() {
    // Arrange
    var components = TraceComponents.Http;

    // Assert - Http is the first flag (2^0 = 1)
    await Assert.That((int)components).IsEqualTo(1);
  }

  [Test]
  public async Task Commands_HasValue_TwoAsync() {
    // Arrange
    var components = TraceComponents.Commands;

    // Assert - Commands is the second flag (2^1 = 2)
    await Assert.That((int)components).IsEqualTo(2);
  }

  [Test]
  public async Task Events_HasValue_FourAsync() {
    // Arrange
    var components = TraceComponents.Events;

    // Assert - Events is the third flag (2^2 = 4)
    await Assert.That((int)components).IsEqualTo(4);
  }

  [Test]
  public async Task Outbox_HasValue_EightAsync() {
    // Arrange
    var components = TraceComponents.Outbox;

    // Assert - Outbox is the fourth flag (2^3 = 8)
    await Assert.That((int)components).IsEqualTo(8);
  }

  [Test]
  public async Task Inbox_HasValue_SixteenAsync() {
    // Arrange
    var components = TraceComponents.Inbox;

    // Assert - Inbox is the fifth flag (2^4 = 16)
    await Assert.That((int)components).IsEqualTo(16);
  }

  [Test]
  public async Task EventStore_HasValue_ThirtyTwoAsync() {
    // Arrange
    var components = TraceComponents.EventStore;

    // Assert - EventStore is the sixth flag (2^5 = 32)
    await Assert.That((int)components).IsEqualTo(32);
  }

  [Test]
  public async Task Handlers_HasValue_SixtyFourAsync() {
    // Arrange
    var components = TraceComponents.Handlers;

    // Assert - Handlers is the seventh flag (2^6 = 64)
    await Assert.That((int)components).IsEqualTo(64);
  }

  [Test]
  public async Task Lifecycle_HasValue_OneHundredTwentyEightAsync() {
    // Arrange
    var components = TraceComponents.Lifecycle;

    // Assert - Lifecycle is the eighth flag (2^7 = 128)
    await Assert.That((int)components).IsEqualTo(128);
  }

  [Test]
  public async Task Perspectives_HasValue_TwoHundredFiftySixAsync() {
    // Arrange
    var components = TraceComponents.Perspectives;

    // Assert - Perspectives is the ninth flag (2^8 = 256)
    await Assert.That((int)components).IsEqualTo(256);
  }

  [Test]
  public async Task Explicit_HasValue_FiveHundredTwelveAsync() {
    // Arrange
    var components = TraceComponents.Explicit;

    // Assert - Explicit is the tenth flag (2^9 = 512)
    await Assert.That((int)components).IsEqualTo(512);
  }

  #endregion

  #region All Composite Value

  [Test]
  public async Task All_IsCombination_OfAllComponentsExceptExplicitAsync() {
    // Arrange
    var all = TraceComponents.All;
    var expected = TraceComponents.Http | TraceComponents.Commands | TraceComponents.Events
                   | TraceComponents.Outbox | TraceComponents.Inbox | TraceComponents.EventStore
                   | TraceComponents.Handlers | TraceComponents.Lifecycle | TraceComponents.Perspectives;

    // Assert - All combines all component flags except Explicit
    await Assert.That(all).IsEqualTo(expected);
  }

  [Test]
  public async Task All_HasValue_FiveHundredElevenAsync() {
    // Arrange
    var all = TraceComponents.All;

    // Assert - All = 1+2+4+8+16+32+64+128+256 = 511
    await Assert.That((int)all).IsEqualTo(511);
  }

  [Test]
  public async Task All_DoesNotInclude_ExplicitAsync() {
    // Arrange
    var all = TraceComponents.All;

    // Assert - All doesn't include Explicit (which is for targeted tracing)
    await Assert.That(all.HasFlag(TraceComponents.Explicit)).IsFalse();
  }

  #endregion

  #region HasFlag Tests

  [Test]
  public async Task All_HasFlag_HttpAsync() {
    // Arrange
    var all = TraceComponents.All;

    // Assert
    await Assert.That(all.HasFlag(TraceComponents.Http)).IsTrue();
  }

  [Test]
  public async Task All_HasFlag_HandlersAsync() {
    // Arrange
    var all = TraceComponents.All;

    // Assert
    await Assert.That(all.HasFlag(TraceComponents.Handlers)).IsTrue();
  }

  [Test]
  public async Task All_HasFlag_EventStoreAsync() {
    // Arrange
    var all = TraceComponents.All;

    // Assert
    await Assert.That(all.HasFlag(TraceComponents.EventStore)).IsTrue();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_AnyComponentAsync() {
    // Arrange
    var none = TraceComponents.None;

    // Assert - None has no flags set
    await Assert.That(none.HasFlag(TraceComponents.Http)).IsFalse();
    await Assert.That(none.HasFlag(TraceComponents.Commands)).IsFalse();
    await Assert.That(none.HasFlag(TraceComponents.Events)).IsFalse();
    await Assert.That(none.HasFlag(TraceComponents.Handlers)).IsFalse();
  }

  [Test]
  public async Task CustomCombination_HasFlag_OnlySelectedComponentsAsync() {
    // Arrange - Common debugging combination
    var debugHandlers = TraceComponents.Handlers | TraceComponents.Lifecycle;

    // Assert
    await Assert.That(debugHandlers.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(debugHandlers.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(debugHandlers.HasFlag(TraceComponents.Http)).IsFalse();
    await Assert.That(debugHandlers.HasFlag(TraceComponents.Events)).IsFalse();
  }

  #endregion

  #region Flag Combination Tests

  [Test]
  public async Task CombiningFlags_ProducesCorrectValueAsync() {
    // Arrange
    var combined = TraceComponents.Http | TraceComponents.Handlers;

    // Assert - 1 | 64 = 65
    await Assert.That((int)combined).IsEqualTo(65);
  }

  [Test]
  public async Task MessageFlow_CombinationAsync() {
    // Arrange - Track messages through the system
    var messageFlow = TraceComponents.Commands
                    | TraceComponents.Events
                    | TraceComponents.Outbox
                    | TraceComponents.Inbox;

    // Assert - 2 | 4 | 8 | 16 = 30
    await Assert.That((int)messageFlow).IsEqualTo(30);

    // Verify individual flags
    await Assert.That(messageFlow.HasFlag(TraceComponents.Commands)).IsTrue();
    await Assert.That(messageFlow.HasFlag(TraceComponents.Events)).IsTrue();
    await Assert.That(messageFlow.HasFlag(TraceComponents.Outbox)).IsTrue();
    await Assert.That(messageFlow.HasFlag(TraceComponents.Inbox)).IsTrue();
    await Assert.That(messageFlow.HasFlag(TraceComponents.Http)).IsFalse();
  }

  [Test]
  public async Task PersistenceOperations_CombinationAsync() {
    // Arrange - Focus on data storage
    var persistence = TraceComponents.EventStore | TraceComponents.Perspectives;

    // Assert - 32 | 256 = 288
    await Assert.That((int)persistence).IsEqualTo(288);
  }

  #endregion

  #region Enum Attribute Tests

  [Test]
  public async Task TraceComponents_HasFlagsAttributeAsync() {
    // Arrange
    var type = typeof(TraceComponents);
    var hasFlagsAttribute = Attribute.IsDefined(type, typeof(FlagsAttribute));

    // Assert
    await Assert.That(hasFlagsAttribute).IsTrue();
  }

  [Test]
  public async Task TraceComponents_HasElevenDefinedValuesAsync() {
    // Arrange
    var allValues = Enum.GetValues<TraceComponents>();

    // Assert - None, Http, Commands, Events, Outbox, Inbox, EventStore, Handlers, Lifecycle, Perspectives, Explicit, All = 12
    await Assert.That(allValues.Length).IsEqualTo(12);
  }

  [Test]
  public async Task TraceComponents_CanBeParsedFromStringAsync() {
    // Assert - All values can be parsed from their string names
    await Assert.That(Enum.TryParse<TraceComponents>("None", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceComponents>("Http", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceComponents>("Commands", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceComponents>("Handlers", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceComponents>("All", out _)).IsTrue();
    await Assert.That(Enum.TryParse<TraceComponents>("Explicit", out _)).IsTrue();
  }

  [Test]
  public async Task TraceComponents_CanBeParsedCaseInsensitiveAsync() {
    // Assert - Values can be parsed case-insensitively (for config binding)
    await Assert.That(Enum.TryParse<TraceComponents>("handlers", true, out var handlers)).IsTrue();
    await Assert.That(handlers).IsEqualTo(TraceComponents.Handlers);

    await Assert.That(Enum.TryParse<TraceComponents>("EVENTSTORE", true, out var eventStore)).IsTrue();
    await Assert.That(eventStore).IsEqualTo(TraceComponents.EventStore);
  }

  [Test]
  public async Task TraceComponents_CommaDelimitedParsing_ForConfigurationAsync() {
    // Arrange - Config binding often passes comma-delimited values
    var parsed = Enum.Parse<TraceComponents>("Http, Handlers, EventStore");

    // Assert - Should combine the flags
    await Assert.That(parsed.HasFlag(TraceComponents.Http)).IsTrue();
    await Assert.That(parsed.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(parsed.HasFlag(TraceComponents.EventStore)).IsTrue();
    await Assert.That(parsed.HasFlag(TraceComponents.Commands)).IsFalse();
  }

  #endregion

  #region Bitwise Operation Tests

  [Test]
  public async Task RemovingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = TraceComponents.All;
    var withoutHttp = initial & ~TraceComponents.Http;

    // Assert
    await Assert.That(withoutHttp.HasFlag(TraceComponents.Http)).IsFalse();
    await Assert.That(withoutHttp.HasFlag(TraceComponents.Handlers)).IsTrue();
  }

  [Test]
  public async Task AddingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = TraceComponents.Handlers;
    var withLifecycle = initial | TraceComponents.Lifecycle;

    // Assert
    await Assert.That(withLifecycle.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(withLifecycle.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(withLifecycle.HasFlag(TraceComponents.Http)).IsFalse();
  }

  [Test]
  public async Task TogglingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = TraceComponents.Handlers | TraceComponents.Http;

    // Toggle Http off
    var toggled = initial ^ TraceComponents.Http;

    // Assert
    await Assert.That(toggled.HasFlag(TraceComponents.Http)).IsFalse();
    await Assert.That(toggled.HasFlag(TraceComponents.Handlers)).IsTrue();

    // Toggle Http back on
    var toggledBack = toggled ^ TraceComponents.Http;
    await Assert.That(toggledBack.HasFlag(TraceComponents.Http)).IsTrue();
  }

  #endregion
}
