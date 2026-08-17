namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Specifies the category of auto-population for a message property.
/// </summary>
/// <remarks>
/// <para>
/// Each kind corresponds to a different source of data:
/// <list type="bullet">
/// <item><see cref="Timestamp"/> - Lifecycle timestamps from message processing</item>
/// <item><see cref="Context"/> - Security context values (UserId, TenantId)</item>
/// <item><see cref="Service"/> - Service instance information</item>
/// <item><see cref="Identifier"/> - Message identifiers (correlation, causation, etc.)</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/AutoPopulateProcessorTests.cs:PopulateKind_AllValues_AreDistinctAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/AutoPopulateProcessorTests.cs:PopulateKind_HasFiveValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/AutoPopulateProcessorTests.cs:AutoPopulateRegistration_RequiredProperties_CanBeSetAsync</tests>
public enum PopulateKind {
  /// <summary>
  /// Populate from lifecycle timestamps (SentAt, QueuedAt, DeliveredAt).
  /// </summary>
  Timestamp = 0,

  /// <summary>
  /// Populate from security context (UserId, TenantId).
  /// </summary>
  Context = 1,

  /// <summary>
  /// Populate from service instance info (ServiceName, InstanceId, HostName, ProcessId).
  /// </summary>
  Service = 2,

  /// <summary>
  /// Populate from message identifiers (MessageId, CorrelationId, CausationId, StreamId).
  /// </summary>
  Identifier = 3,

  /// <summary>
  /// Populate from an inbound HTTP header captured at the request edge (rides the scope extensions).
  /// </summary>
  Header = 4
}
