using System.Globalization;
using System.Text.Json;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Policies;

/// <summary>
/// Universal context that flows through the entire execution pipeline.
/// Provides message information, runtime context, and helpers for policy evaluation.
/// Accessible to both internal Whizbang code and user code.
/// </summary>
/// <remarks>
/// This class is designed to be pooled to minimize heap allocations.
/// Use PolicyContextPool to rent and return instances.
/// </remarks>
/// <docs>infrastructure/policies</docs>
public class PolicyContext {
  /// <summary>
  /// The message being processed.
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_InitializesWithMessage_SetsMessageAndMessageTypeAsync</tests>
  public object Message { get; private set; }

  /// <summary>
  /// The runtime type of the message.
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_InitializesWithMessage_SetsMessageAndMessageTypeAsync</tests>
  public Type MessageType { get; private set; }

  /// <summary>
  /// The message envelope containing metadata and routing information.
  /// May be null if message hasn't been wrapped yet.
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_WithEnvelope_SetsEnvelopeAsync</tests>
  public IMessageEnvelope? Envelope { get; private set; }

  /// <summary>
  /// The service provider for dependency injection.
  /// May be null if not configured.
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_WithServiceProvider_SetsServicesAsync</tests>
  public IServiceProvider? Services { get; private set; }

  /// <summary>
  /// The environment name (e.g., "development", "staging", "production").
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_WithEnvironment_SetsEnvironmentAsync</tests>
  public string Environment { get; private set; }

  /// <summary>
  /// When this context was created (approximately when message processing started).
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_SetsExecutionTime_ToApproximatelyNowAsync</tests>
  public DateTimeOffset ExecutionTime { get; private set; }

  /// <summary>
  /// Policy decision trail for recording all policy decisions made during processing.
  /// </summary>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:Constructor_InitializesTrail_CreatesEmptyDecisionTrailAsync</tests>
  public PolicyDecisionTrail Trail { get; private set; }

  /// <summary>
  /// Creates a new PolicyContext with the specified message.
  /// </summary>
  public PolicyContext(
      object message,
      IMessageEnvelope? envelope = null,
      IServiceProvider? services = null,
      string environment = "development"
  ) {
    Message = message ?? throw new ArgumentNullException(nameof(message));
    MessageType = message.GetType();
    Envelope = envelope;
    Services = services;
    Environment = environment;
    ExecutionTime = DateTimeOffset.UtcNow;
    Trail = new PolicyDecisionTrail();
  }

  /// <summary>
  /// Internal parameterless constructor for pooling.
  /// </summary>
  internal PolicyContext() {
    Message = null!;
    MessageType = null!;
    Environment = "development";
    Trail = new PolicyDecisionTrail();
  }

  /// <summary>
  /// Initializes the context with new values. Used by the pool.
  /// </summary>
  internal void Initialize(
      object message,
      IMessageEnvelope? envelope,
      IServiceProvider? services,
      string environment
  ) {
    Message = message ?? throw new ArgumentNullException(nameof(message));
    MessageType = message.GetType();
    Envelope = envelope;
    Services = services;
    Environment = environment;
    ExecutionTime = DateTimeOffset.UtcNow;
    Trail = new PolicyDecisionTrail();
  }

  /// <summary>
  /// Resets the context for return to the pool.
  /// </summary>
  internal void Reset() {
    Message = null!;
    MessageType = null!;
    Envelope = null;
    Services = null;
    Environment = "development";
    Trail = new PolicyDecisionTrail();
  }

  /// <summary>
  /// Gets a service from the service provider.
  /// </summary>
  /// <typeparam name="T">The service type</typeparam>
  /// <returns>The service instance</returns>
  /// <exception cref="InvalidOperationException">If service provider is not configured</exception>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetService_ReturnsService_WhenServiceProviderSetAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetService_ThrowsException_WhenServiceProviderNotSetAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetService_ThrowsException_WhenServiceNotRegisteredAsync</tests>
  public T GetService<T>() where T : class {
    if (Services is null) {
      throw new InvalidOperationException(
          "ServiceProvider is not configured. " +
          "Ensure PolicyContext is created with a valid IServiceProvider."
      );
    }

    var service = Services.GetService(typeof(T)) as T ?? throw new InvalidOperationException(
          $"Service of type {typeof(T).Name} is not registered in the ServiceProvider."
      );
    return service;
  }

  /// <summary>
  /// Gets metadata value from the message envelope.
  /// Uses the envelope's GetMetadata method - zero reflection.
  /// </summary>
  /// <param name="key">The metadata key</param>
  /// <returns>The metadata value, or null if not found</returns>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetMetadata_ReturnsValue_WhenKeyExistsAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetMetadata_ReturnsNull_WhenKeyDoesNotExistAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetMetadata_ReturnsNull_WhenEnvelopeNotSetAsync</tests>
  public object? GetMetadata(string key) {
    return Envelope?.GetMetadata(key);
  }

  /// <summary>
  /// Checks if a specific tag is present in the message metadata.
  /// Tags are stored in metadata under the "tags" key as a string array.
  /// </summary>
  /// <param name="tag">The tag to check for</param>
  /// <returns>True if the tag is present, false otherwise</returns>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasTag_ReturnsTrue_WhenTagExistsInMetadataAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasTag_ReturnsFalse_WhenTagDoesNotExistAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasTag_ReturnsFalse_WhenNoTagsInMetadataAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasTag_ReturnsTrue_WhenTagsAreIEnumerableAsync</tests>
  public bool HasTag(string tag) {
    var tags = GetMetadata("tags");
    if (tags is null) {
      return false;
    }

    // Handle JsonElement (returned from MessageEnvelope.GetMetadata)
    if (tags is JsonElement jsonElement) {
      if (jsonElement.ValueKind != JsonValueKind.Array) {
        return false;
      }
      foreach (var item in jsonElement.EnumerateArray()) {
        if (item.ValueKind == JsonValueKind.String && item.GetString() == tag) {
          return true;
        }
      }
      return false;
    }

    // Handle direct string arrays (for backwards compatibility)
    if (tags is string[] tagArray) {
      return tagArray.Contains(tag);
    }
    if (tags is IEnumerable<string> tagEnumerable) {
      return tagEnumerable.Contains(tag);
    }
    return false;
  }

  /// <summary>
  /// Checks if a specific flag is set in the message metadata.
  /// Flags are stored in metadata under the "flags" key as an enum value.
  /// </summary>
  /// <param name="flag">The flag to check for</param>
  /// <returns>True if the flag is set, false otherwise</returns>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasFlag_ReturnsTrue_WhenFlagIsSetAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasFlag_ReturnsFalse_WhenFlagIsNotSetAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:HasFlag_ReturnsFalse_WhenNoFlagsInMetadataAsync</tests>
  public bool HasFlag(Enum flag) {
    var flags = GetMetadata("flags");
    if (flags is null) {
      return false;
    }

    // Handle JsonElement (returned from MessageEnvelope.GetMetadata)
    if (flags is JsonElement jsonElement) {
      if (jsonElement.ValueKind != JsonValueKind.Number) {
        return false;
      }
      var flagsValue = jsonElement.GetInt64();
      var targetFlagValue = Convert.ToInt64(flag, CultureInfo.InvariantCulture);
      return (flagsValue & targetFlagValue) == targetFlagValue;
    }

    // Handle direct numeric values (for backwards compatibility)
    var flagsNumeric = Convert.ToInt64(flags, CultureInfo.InvariantCulture);
    var targetFlag = Convert.ToInt64(flag, CultureInfo.InvariantCulture);
    return (flagsNumeric & targetFlag) == targetFlag;
  }

  /// <summary>
  /// Checks if the message is for a specific aggregate type.
  /// Uses naming convention: message type should contain aggregate type name.
  /// Examples: CreateOrder → Order, UpdateCustomer → Customer
  /// </summary>
  /// <typeparam name="TAggregate">The aggregate type to check for</typeparam>
  /// <returns>True if message is for this aggregate type</returns>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:MatchesAggregate_ReturnsTrue_WhenMessageIsForSpecifiedAggregateTypeAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:MatchesAggregate_ReturnsFalse_WhenMessageIsForDifferentAggregateTypeAsync</tests>
  public bool MatchesAggregate<TAggregate>() {
    var aggregateName = typeof(TAggregate).Name;
    var messageName = MessageType.Name;

    // Check if message name contains aggregate name
    // Examples: CreateOrder contains "Order", OrderCreated contains "Order"
    return messageName.Contains(aggregateName, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Gets the aggregate ID from the message using source-generated extractors.
  /// The message type must have a property marked with [AggregateId] attribute.
  /// Zero reflection - uses DI-injected extractor for optimal performance.
  /// </summary>
  /// <returns>The aggregate ID</returns>
  /// <exception cref="InvalidOperationException">If aggregate ID extractor is not registered or aggregate ID is not found</exception>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetAggregateId_WithAggregateIdAttribute_UsesGeneratedExtractorAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetAggregateId_WithoutAggregateIdAttribute_ThrowsHelpfulExceptionAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetAggregateId_ReturnsId_WhenMessageContainsAggregateIdAsync</tests>
  /// <tests>Whizbang.Policies.Tests/PolicyContextTests.cs:GetAggregateId_ThrowsException_WhenMessageDoesNotContainAggregateIdAsync</tests>
  public Guid GetAggregateId() {
    // Get the DI-injected extractor (zero reflection)
    if (Services is null) {
      throw new InvalidOperationException(
          "ServiceProvider is not configured. " +
          "Ensure PolicyContext is created with a valid IServiceProvider that includes aggregate ID extraction. " +
          "Call services.AddWhizbangAggregateIdExtractor() during startup."
      );
    }

    var extractor = Services.GetService(typeof(IAggregateIdExtractor)) as IAggregateIdExtractor ?? throw new InvalidOperationException(
          "IAggregateIdExtractor is not registered in the ServiceProvider. " +
          "Call services.AddWhizbangAggregateIdExtractor() during startup to register the source-generated extractor."
      );

    // Use the source-generated extractor (zero reflection)
    var aggregateId = extractor.ExtractAggregateId(Message, MessageType);
    if (aggregateId.HasValue) {
      return aggregateId.Value;
    }

    throw new InvalidOperationException(
        $"Message type {MessageType.Name} does not have a property marked with [AggregateId] attribute. " +
        $"Add [AggregateId] to a Guid property to enable aggregate ID extraction."
    );
  }
}
