using Whizbang.Core;

namespace Whizbang.Sagas;

/// <summary>
/// Base interface for any event that participates in a saga's lifecycle.
/// Carries the saga's name and the underlying domain entity id; the
/// stream id is inherited from the consumer's concrete event base.
/// </summary>
public interface ISagaEvent : IEvent {

  /// <summary>Saga name (e.g. <c>"BulkImport"</c>); matches the value passed to <c>[Saga("Name")]</c>.</summary>
  string SagaName { get; }

  /// <summary>Consumer-domain entity id (tenant id, operation id, etc.) carried for routing and filtering.</summary>
  Guid EntityId { get; }
}
