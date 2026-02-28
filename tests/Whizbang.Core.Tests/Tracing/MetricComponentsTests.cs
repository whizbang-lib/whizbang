using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for MetricComponents flags enum which controls which system components emit metrics.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/MetricComponents.cs</code-under-test>
public class MetricComponentsTests {
  #region Base Flag Values (Powers of 2)

  [Test]
  public async Task None_HasValue_ZeroAsync() {
    // Arrange
    var components = MetricComponents.None;

    // Assert
    await Assert.That((int)components).IsEqualTo(0);
  }

  [Test]
  public async Task Handlers_HasValue_OneAsync() {
    // Arrange
    var components = MetricComponents.Handlers;

    // Assert - Handlers is the first flag (2^0 = 1)
    await Assert.That((int)components).IsEqualTo(1);
  }

  [Test]
  public async Task Dispatcher_HasValue_TwoAsync() {
    // Arrange
    var components = MetricComponents.Dispatcher;

    // Assert - Dispatcher is the second flag (2^1 = 2)
    await Assert.That((int)components).IsEqualTo(2);
  }

  [Test]
  public async Task Messages_HasValue_FourAsync() {
    // Arrange
    var components = MetricComponents.Messages;

    // Assert - Messages is the third flag (2^2 = 4)
    await Assert.That((int)components).IsEqualTo(4);
  }

  [Test]
  public async Task Events_HasValue_EightAsync() {
    // Arrange
    var components = MetricComponents.Events;

    // Assert - Events is the fourth flag (2^3 = 8)
    await Assert.That((int)components).IsEqualTo(8);
  }

  [Test]
  public async Task Outbox_HasValue_SixteenAsync() {
    // Arrange
    var components = MetricComponents.Outbox;

    // Assert - Outbox is the fifth flag (2^4 = 16)
    await Assert.That((int)components).IsEqualTo(16);
  }

  [Test]
  public async Task Inbox_HasValue_ThirtyTwoAsync() {
    // Arrange
    var components = MetricComponents.Inbox;

    // Assert - Inbox is the sixth flag (2^5 = 32)
    await Assert.That((int)components).IsEqualTo(32);
  }

  [Test]
  public async Task EventStore_HasValue_SixtyFourAsync() {
    // Arrange
    var components = MetricComponents.EventStore;

    // Assert - EventStore is the seventh flag (2^6 = 64)
    await Assert.That((int)components).IsEqualTo(64);
  }

  [Test]
  public async Task Lifecycle_HasValue_OneHundredTwentyEightAsync() {
    // Arrange
    var components = MetricComponents.Lifecycle;

    // Assert - Lifecycle is the eighth flag (2^7 = 128)
    await Assert.That((int)components).IsEqualTo(128);
  }

  [Test]
  public async Task Perspectives_HasValue_TwoHundredFiftySixAsync() {
    // Arrange
    var components = MetricComponents.Perspectives;

    // Assert - Perspectives is the ninth flag (2^8 = 256)
    await Assert.That((int)components).IsEqualTo(256);
  }

  [Test]
  public async Task Tags_HasValue_FiveHundredTwelveAsync() {
    // Arrange
    var components = MetricComponents.Tags;

    // Assert - Tags is the tenth flag (2^9 = 512)
    await Assert.That((int)components).IsEqualTo(512);
  }

  [Test]
  public async Task Security_HasValue_OneThousandTwentyFourAsync() {
    // Arrange
    var components = MetricComponents.Security;

    // Assert - Security is the eleventh flag (2^10 = 1024)
    await Assert.That((int)components).IsEqualTo(1024);
  }

  [Test]
  public async Task Workers_HasValue_TwoThousandFortyEightAsync() {
    // Arrange
    var components = MetricComponents.Workers;

    // Assert - Workers is the twelfth flag (2^11 = 2048)
    await Assert.That((int)components).IsEqualTo(2048);
  }

  [Test]
  public async Task Errors_HasValue_FourThousandNinetySixAsync() {
    // Arrange
    var components = MetricComponents.Errors;

    // Assert - Errors is the thirteenth flag (2^12 = 4096)
    await Assert.That((int)components).IsEqualTo(4096);
  }

  [Test]
  public async Task Policies_HasValue_EightThousandOneHundredNinetyTwoAsync() {
    // Arrange
    var components = MetricComponents.Policies;

    // Assert - Policies is the fourteenth flag (2^13 = 8192)
    await Assert.That((int)components).IsEqualTo(8192);
  }

  #endregion

  #region All Composite Value

  [Test]
  public async Task All_IsCombination_OfAllComponentsAsync() {
    // Arrange
    var all = MetricComponents.All;
    var expected = MetricComponents.Handlers | MetricComponents.Dispatcher | MetricComponents.Messages
                   | MetricComponents.Events | MetricComponents.Outbox | MetricComponents.Inbox
                   | MetricComponents.EventStore | MetricComponents.Lifecycle | MetricComponents.Perspectives
                   | MetricComponents.Tags | MetricComponents.Security | MetricComponents.Workers
                   | MetricComponents.Errors | MetricComponents.Policies;

    // Assert - All combines all component flags
    await Assert.That(all).IsEqualTo(expected);
  }

  [Test]
  public async Task All_HasValue_SixteenThreeEightyThreeAsync() {
    // Arrange
    var all = MetricComponents.All;

    // Assert - All = 1+2+4+8+16+32+64+128+256+512+1024+2048+4096+8192 = 16383
    await Assert.That((int)all).IsEqualTo(16383);
  }

  #endregion

  #region HasFlag Tests

  [Test]
  public async Task All_HasFlag_HandlersAsync() {
    // Arrange
    var all = MetricComponents.All;

    // Assert
    await Assert.That(all.HasFlag(MetricComponents.Handlers)).IsTrue();
  }

  [Test]
  public async Task All_HasFlag_EventStoreAsync() {
    // Arrange
    var all = MetricComponents.All;

    // Assert
    await Assert.That(all.HasFlag(MetricComponents.EventStore)).IsTrue();
  }

  [Test]
  public async Task All_HasFlag_ErrorsAsync() {
    // Arrange
    var all = MetricComponents.All;

    // Assert
    await Assert.That(all.HasFlag(MetricComponents.Errors)).IsTrue();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_AnyComponentAsync() {
    // Arrange
    var none = MetricComponents.None;

    // Assert - None has no flags set
    await Assert.That(none.HasFlag(MetricComponents.Handlers)).IsFalse();
    await Assert.That(none.HasFlag(MetricComponents.Dispatcher)).IsFalse();
    await Assert.That(none.HasFlag(MetricComponents.Events)).IsFalse();
    await Assert.That(none.HasFlag(MetricComponents.Errors)).IsFalse();
  }

  [Test]
  public async Task CustomCombination_HasFlag_OnlySelectedComponentsAsync() {
    // Arrange - Common production combination
    var production = MetricComponents.Handlers | MetricComponents.EventStore | MetricComponents.Errors;

    // Assert
    await Assert.That(production.HasFlag(MetricComponents.Handlers)).IsTrue();
    await Assert.That(production.HasFlag(MetricComponents.EventStore)).IsTrue();
    await Assert.That(production.HasFlag(MetricComponents.Errors)).IsTrue();
    await Assert.That(production.HasFlag(MetricComponents.Dispatcher)).IsFalse();
    await Assert.That(production.HasFlag(MetricComponents.Messages)).IsFalse();
  }

  #endregion

  #region Flag Combination Tests

  [Test]
  public async Task CombiningFlags_ProducesCorrectValueAsync() {
    // Arrange
    var combined = MetricComponents.Handlers | MetricComponents.Errors;

    // Assert - 1 | 4096 = 4097
    await Assert.That((int)combined).IsEqualTo(4097);
  }

  [Test]
  public async Task MessageFlow_CombinationAsync() {
    // Arrange - Track messages through the system
    var messageFlow = MetricComponents.Messages
                    | MetricComponents.Events
                    | MetricComponents.Outbox
                    | MetricComponents.Inbox;

    // Assert - 4 | 8 | 16 | 32 = 60
    await Assert.That((int)messageFlow).IsEqualTo(60);

    // Verify individual flags
    await Assert.That(messageFlow.HasFlag(MetricComponents.Messages)).IsTrue();
    await Assert.That(messageFlow.HasFlag(MetricComponents.Events)).IsTrue();
    await Assert.That(messageFlow.HasFlag(MetricComponents.Outbox)).IsTrue();
    await Assert.That(messageFlow.HasFlag(MetricComponents.Inbox)).IsTrue();
    await Assert.That(messageFlow.HasFlag(MetricComponents.Handlers)).IsFalse();
  }

  [Test]
  public async Task PersistenceOperations_CombinationAsync() {
    // Arrange - Focus on data storage
    var persistence = MetricComponents.EventStore | MetricComponents.Perspectives;

    // Assert - 64 | 256 = 320
    await Assert.That((int)persistence).IsEqualTo(320);
  }

  #endregion

  #region Enum Attribute Tests

  [Test]
  public async Task MetricComponents_HasFlagsAttributeAsync() {
    // Arrange
    var type = typeof(MetricComponents);
    var hasFlagsAttribute = Attribute.IsDefined(type, typeof(FlagsAttribute));

    // Assert
    await Assert.That(hasFlagsAttribute).IsTrue();
  }

  [Test]
  public async Task MetricComponents_HasSixteenDefinedValuesAsync() {
    // Arrange
    var allValues = Enum.GetValues<MetricComponents>();

    // Assert - None + 14 components + All = 16
    await Assert.That(allValues.Length).IsEqualTo(16);
  }

  [Test]
  public async Task MetricComponents_CanBeParsedFromStringAsync() {
    // Assert - All values can be parsed from their string names
    await Assert.That(Enum.TryParse<MetricComponents>("None", out _)).IsTrue();
    await Assert.That(Enum.TryParse<MetricComponents>("Handlers", out _)).IsTrue();
    await Assert.That(Enum.TryParse<MetricComponents>("EventStore", out _)).IsTrue();
    await Assert.That(Enum.TryParse<MetricComponents>("Errors", out _)).IsTrue();
    await Assert.That(Enum.TryParse<MetricComponents>("All", out _)).IsTrue();
  }

  [Test]
  public async Task MetricComponents_CanBeParsedCaseInsensitiveAsync() {
    // Assert - Values can be parsed case-insensitively (for config binding)
    await Assert.That(Enum.TryParse<MetricComponents>("handlers", true, out var handlers)).IsTrue();
    await Assert.That(handlers).IsEqualTo(MetricComponents.Handlers);

    await Assert.That(Enum.TryParse<MetricComponents>("EVENTSTORE", true, out var eventStore)).IsTrue();
    await Assert.That(eventStore).IsEqualTo(MetricComponents.EventStore);
  }

  [Test]
  public async Task MetricComponents_CommaDelimitedParsing_ForConfigurationAsync() {
    // Arrange - Config binding often passes comma-delimited values
    var parsed = Enum.Parse<MetricComponents>("Handlers, EventStore, Errors");

    // Assert - Should combine the flags
    await Assert.That(parsed.HasFlag(MetricComponents.Handlers)).IsTrue();
    await Assert.That(parsed.HasFlag(MetricComponents.EventStore)).IsTrue();
    await Assert.That(parsed.HasFlag(MetricComponents.Errors)).IsTrue();
    await Assert.That(parsed.HasFlag(MetricComponents.Dispatcher)).IsFalse();
  }

  #endregion

  #region Bitwise Operation Tests

  [Test]
  public async Task RemovingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = MetricComponents.All;
    var withoutHandlers = initial & ~MetricComponents.Handlers;

    // Assert
    await Assert.That(withoutHandlers.HasFlag(MetricComponents.Handlers)).IsFalse();
    await Assert.That(withoutHandlers.HasFlag(MetricComponents.EventStore)).IsTrue();
  }

  [Test]
  public async Task AddingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = MetricComponents.Handlers;
    var withErrors = initial | MetricComponents.Errors;

    // Assert
    await Assert.That(withErrors.HasFlag(MetricComponents.Handlers)).IsTrue();
    await Assert.That(withErrors.HasFlag(MetricComponents.Errors)).IsTrue();
    await Assert.That(withErrors.HasFlag(MetricComponents.Dispatcher)).IsFalse();
  }

  [Test]
  public async Task TogglingFlag_WorksCorrectlyAsync() {
    // Arrange
    var initial = MetricComponents.Handlers | MetricComponents.Errors;

    // Toggle Errors off
    var toggled = initial ^ MetricComponents.Errors;

    // Assert
    await Assert.That(toggled.HasFlag(MetricComponents.Errors)).IsFalse();
    await Assert.That(toggled.HasFlag(MetricComponents.Handlers)).IsTrue();

    // Toggle Errors back on
    var toggledBack = toggled ^ MetricComponents.Errors;
    await Assert.That(toggledBack.HasFlag(MetricComponents.Errors)).IsTrue();
  }

  #endregion
}
