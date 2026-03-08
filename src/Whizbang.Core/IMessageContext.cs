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
  /// Optional tenant identifier for multi-tenant isolation.
  /// </summary>
  /// <remarks>
  /// <para>
  /// In lifecycle receptors (especially deferred stages like <c>PostPerspectiveAsync</c>),
  /// use this property instead of HTTP-based tenant resolution since the original HTTP
  /// context is unavailable.
  /// </para>
  /// <para>
  /// The tenant ID propagates through the message envelope's security context hops,
  /// ensuring consistent tenant context throughout the message's lifecycle.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:TenantId_IsNullByDefaultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
  string? TenantId { get; }

  /// <summary>
  /// Additional metadata for cross-cutting concerns.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Metadata_IsEmptyByDefaultAsync</tests>
  IReadOnlyDictionary<string, object> Metadata { get; }

  /// <summary>
  /// Gets the rich authorization context (Roles, Permissions, SecurityPrincipals, Claims)
  /// that this message context OWNS and carries.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Messages carry state in event-sourced systems. The ScopeContext is OWNED by
  /// the message context, not read from ambient AsyncLocal.
  /// </para>
  /// <para>
  /// When a message context is created, it captures the current scope context.
  /// AsyncLocal then reads FROM the initiating message context's ScopeContext.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/cascade-context#scope-context</docs>
  Security.IScopeContext? ScopeContext { get; }
}
