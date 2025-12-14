namespace Whizbang.Core;

/// <summary>
/// Marker interface for events - messages that represent facts about state changes that have occurred.
/// Events are emitted by Receptors and processed by Perspectives to update read models.
/// </summary>
/// <docs>messaging/commands-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:EventStoreContractTests</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithPropertyAttribute_GeneratesExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithMultipleEvents_GeneratesAllExtractorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_ReportsDiagnostic_ForEventWithNoStreamKeyAsync</tests>
public interface IEvent {
}
