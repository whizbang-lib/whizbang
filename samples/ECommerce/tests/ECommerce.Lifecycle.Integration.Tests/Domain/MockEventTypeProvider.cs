using Whizbang.Core.Messaging;

namespace ECommerce.Lifecycle.Integration.Tests.Domain;

/// <summary>
/// Provides event type mappings for mock batch test events.
/// Required by lifecycle message deserialization.
/// </summary>
public class MockEventTypeProvider : IEventTypeProvider {
  private static readonly IReadOnlyList<Type> _eventTypes = [
    typeof(MockBatchTestEvent),
    typeof(MockBatchNoiseEvent)
  ];

  public IReadOnlyList<Type> GetEventTypes() => _eventTypes;
}
