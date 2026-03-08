using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:DefaultConstructor_InitializesRequiredProperties_AutomaticallyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesAllNewIdentifiersAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesUniqueMessageIds_AcrossMultipleCallsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Metadata_IsEmptyByDefaultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:UserId_IsNullByDefaultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Properties_CanBeSetViaInitializer_WithInitSyntaxAsync</tests>
/// Default implementation of <see cref="IMessageContext"/>.
/// </summary>
/// <docs>core-concepts/message-context</docs>
public class MessageContext : IMessageContext {
  /// <inheritdoc />
  public MessageId MessageId { get; init; } = MessageId.New();

  /// <inheritdoc />
  public CorrelationId CorrelationId { get; init; }

  /// <inheritdoc />
  public MessageId CausationId { get; init; }

  /// <inheritdoc />
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

  /// <inheritdoc />
  public string? UserId { get; init; }

  /// <inheritdoc />
  public string? TenantId { get; init; }

  private readonly Dictionary<string, object> _metadata = [];

  /// <inheritdoc />
  public IReadOnlyDictionary<string, object> Metadata => _metadata;

  /// <summary>
  /// Creates a new context with a new MessageId and the specified CorrelationId.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync</tests>
  public static MessageContext Create(CorrelationId correlationId, MessageId? causationId = null) {
    return new MessageContext {
      CorrelationId = correlationId,
      CausationId = causationId ?? MessageId.New()
    };
  }

  /// <summary>
  /// Creates a new context from a CascadeContext.
  /// Copies CorrelationId, CausationId, and SecurityContext from the cascade.
  /// Generates a new MessageId.
  /// </summary>
  /// <param name="cascade">The cascade context to create from</param>
  /// <returns>A new MessageContext with data from the cascade</returns>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_CopiesCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_UsesCascadeCausationIdAsContextCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_GeneratesNewMessageIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_CopiesSecurityContextAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_WithNullSecurityContext_SetsNullSecurityAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_ThrowsOnNullCascadeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:Create_WithCascadeContext_GeneratesUniqueMessageIds_AcrossMultipleCallsAsync</tests>
  public static MessageContext Create(CascadeContext cascade) {
    ArgumentNullException.ThrowIfNull(cascade);

    return new MessageContext {
      CorrelationId = cascade.CorrelationId,
      CausationId = cascade.CausationId,
      UserId = cascade.SecurityContext?.UserId,
      TenantId = cascade.SecurityContext?.TenantId
    };
  }

  /// <summary>
  /// Creates a new context with new identifiers.
  /// If security context is available via <see cref="ScopeContextAccessor.CurrentContext"/>,
  /// automatically inherits UserId and TenantId from the current scope.
  /// This ensures security context propagates through LocalInvokeAsync calls.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesAllNewIdentifiersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/MessageContextTests.cs:New_GeneratesUniqueMessageIds_AcrossMultipleCallsAsync</tests>
  public static MessageContext New() {
    // Read security context from current scope (AsyncLocal)
    string? userId = null;
    string? tenantId = null;
    var scopeContext = ScopeContextAccessor.CurrentContext;
    if (scopeContext is not null) {
      userId = scopeContext.Scope.UserId;
      tenantId = scopeContext.Scope.TenantId;
    }

    return new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = userId,
      TenantId = tenantId
    };
  }
}
