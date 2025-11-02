using System.Reflection;

namespace Whizbang.Core.Policies;

/// <summary>
/// Universal context that flows through the entire execution pipeline.
/// Provides message information, runtime context, and helpers for policy evaluation.
/// Accessible to both internal Whizbang code and user code.
/// </summary>
public class PolicyContext {
  /// <summary>
  /// The message being processed.
  /// </summary>
  public object Message { get; }

  /// <summary>
  /// The runtime type of the message.
  /// </summary>
  public Type MessageType { get; }

  /// <summary>
  /// The message envelope containing metadata and routing information.
  /// May be null if message hasn't been wrapped yet.
  /// </summary>
  public object? Envelope { get; }

  /// <summary>
  /// The service provider for dependency injection.
  /// May be null if not configured.
  /// </summary>
  public IServiceProvider? Services { get; }

  /// <summary>
  /// The environment name (e.g., "development", "staging", "production").
  /// </summary>
  public string Environment { get; }

  /// <summary>
  /// When this context was created (approximately when message processing started).
  /// </summary>
  public DateTimeOffset ExecutionTime { get; }

  /// <summary>
  /// Policy decision trail for recording all policy decisions made during processing.
  /// </summary>
  public PolicyDecisionTrail Trail { get; }

  /// <summary>
  /// Creates a new PolicyContext with the specified message.
  /// </summary>
  public PolicyContext(
      object message,
      object? envelope = null,
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
  /// Gets a service from the service provider.
  /// </summary>
  /// <typeparam name="T">The service type</typeparam>
  /// <returns>The service instance</returns>
  /// <exception cref="InvalidOperationException">If service provider is not configured</exception>
  public T GetService<T>() where T : class {
    if (Services is null) {
      throw new InvalidOperationException(
          "ServiceProvider is not configured. " +
          "Ensure PolicyContext is created with a valid IServiceProvider."
      );
    }

    var service = Services.GetService(typeof(T)) as T;
    if (service is null) {
      throw new InvalidOperationException(
          $"Service of type {typeof(T).Name} is not registered in the ServiceProvider."
      );
    }

    return service;
  }

  /// <summary>
  /// Gets metadata value from the message envelope.
  /// </summary>
  /// <param name="key">The metadata key</param>
  /// <returns>The metadata value, or null if not found</returns>
  public object? GetMetadata(string key) {
    if (Envelope is null) {
      return null;
    }

    // Use reflection to get Metadata property from envelope
    var metadataProperty = Envelope.GetType().GetProperty("Metadata");
    if (metadataProperty is null) {
      return null;
    }

    var metadata = metadataProperty.GetValue(Envelope) as IReadOnlyDictionary<string, object>;
    return metadata?.TryGetValue(key, out var value) == true ? value : null;
  }

  /// <summary>
  /// Checks if a specific tag is present in the message metadata.
  /// Tags are stored in metadata under the "tags" key as a string array.
  /// </summary>
  /// <param name="tag">The tag to check for</param>
  /// <returns>True if the tag is present, false otherwise</returns>
  public bool HasTag(string tag) {
    var tags = GetMetadata("tags");
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
  public bool HasFlag(Enum flag) {
    var flags = GetMetadata("flags");
    if (flags is null) {
      return false;
    }

    // Convert both to underlying type for comparison
    var flagsValue = Convert.ToInt64(flags);
    var targetFlagValue = Convert.ToInt64(flag);

    return (flagsValue & targetFlagValue) == targetFlagValue;
  }

  /// <summary>
  /// Checks if the message is for a specific aggregate type.
  /// Uses naming convention: message type should contain aggregate type name.
  /// Examples: CreateOrder → Order, UpdateCustomer → Customer
  /// </summary>
  /// <typeparam name="TAggregate">The aggregate type to check for</typeparam>
  /// <returns>True if message is for this aggregate type</returns>
  public bool MatchesAggregate<TAggregate>() {
    var aggregateName = typeof(TAggregate).Name;
    var messageName = MessageType.Name;

    // Check if message name contains aggregate name
    // Examples: CreateOrder contains "Order", OrderCreated contains "Order"
    return messageName.Contains(aggregateName, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Gets the aggregate ID from the message.
  /// Looks for a property named "{AggregateName}Id" (e.g., OrderId, CustomerId).
  /// </summary>
  /// <returns>The aggregate ID</returns>
  /// <exception cref="InvalidOperationException">If aggregate ID property is not found</exception>
  public Guid GetAggregateId() {
    // Look for properties ending with "Id"
    var idProperties = MessageType.GetProperties()
        .Where(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) &&
                   (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)))
        .ToList();

    if (!idProperties.Any()) {
      throw new InvalidOperationException(
          $"Message type {MessageType.Name} does not contain an aggregate ID property. " +
          $"Expected a property ending with 'Id' of type Guid."
      );
    }

    // Prefer the first property (most specific)
    var idProperty = idProperties.First();
    var value = idProperty.GetValue(Message);

    if (value is null) {
      throw new InvalidOperationException(
          $"Aggregate ID property {idProperty.Name} is null."
      );
    }

    return value is Guid guid ? guid : (Guid)value;
  }
}
