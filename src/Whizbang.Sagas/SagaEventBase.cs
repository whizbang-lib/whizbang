using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Sagas;

/// <summary>
/// Default base class for saga events when the consumer hasn't supplied
/// their own via <c>[Saga&lt;TBase&gt;("Name")]</c>. Implements the
/// framework's <see cref="IEvent"/> contract with the minimum the
/// dispatcher, outbox, and perspective runners require.
/// </summary>
/// <remarks>
/// <para>
/// Not abstract — the generic attribute's <c>new()</c> constraint
/// requires an instantiable type, and projects that explicitly pass
/// <c>SagaEventBase</c> via <c>[Saga&lt;SagaEventBase&gt;("Name")]</c>
/// must be able to construct it directly.
/// </para>
/// <para>
/// Consumers with their own event hierarchy (audit metadata, tenant
/// context, notification tags, etc.) supply that type to
/// <c>[Saga&lt;TBase&gt;("Name")]</c> instead. Whizbang.Sagas never
/// sees <c>SagaEventBase</c> in their pipeline.
/// </para>
/// </remarks>
public class SagaEventBase : IEvent {

  /// <summary>Globally unique id for this event instance. Defaults to a UUIDv7 (sortable, time-prefixed).</summary>
  public Guid MessageId { get; set; } = TrackedGuid.NewMedo();

  /// <summary>Wall-clock instant the event was constructed.</summary>
  public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

  /// <summary>Optional correlation id propagated from the originating command/operation.</summary>
  public Guid? CorrelationId { get; set; }

  /// <summary>Optional id of the command/event whose handler emitted this event.</summary>
  public Guid? CausationId { get; set; }

  /// <summary>
  /// Optional human-meaningful name of the operation that produced this
  /// event (e.g. <c>"import-bulk-jobs-prod-2026-06-22"</c>). Surfaces in
  /// telemetry dashboards so an operator can filter "all events from this
  /// specific run" without each consumer wiring its own field.
  /// </summary>
  public string? OperationName { get; set; }
}
