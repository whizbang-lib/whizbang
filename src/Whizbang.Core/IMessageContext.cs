using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Provides context and metadata for a message flowing through the system.
/// </summary>
/// <docs>core-concepts/message-context</docs>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs</tests>
public interface IMessageContext {
  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:DefaultConstructor_InitializesRequiredProperties_AutomaticallyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesAllNewIdentifiersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesUniqueMessageIds_AcrossMultipleCallsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  MessageId MessageId { get; }

  /// <summary>
  /// Identifies the logical workflow this message belongs to.
  /// All messages in a workflow share the same CorrelationId.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesAllNewIdentifiersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  CorrelationId CorrelationId { get; }

  /// <summary>
  /// Identifies the message that caused this message to be created.
  /// Forms a causal chain for event sourcing and distributed tracing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesAllNewIdentifiersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  MessageId CausationId { get; }

  /// <summary>
  /// When this message was created.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:DefaultConstructor_InitializesRequiredProperties_AutomaticallyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  DateTimeOffset Timestamp { get; }

  /// <summary>
  /// Optional user identifier for authorization and auditing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:UserId_IsNullByDefaultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  string? UserId { get; }

  /// <summary>
  /// Additional metadata for cross-cutting concerns.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Metadata_IsEmptyByDefaultAsync</tests>
  IReadOnlyDictionary<string, object> Metadata { get; }
}
