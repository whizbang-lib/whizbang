namespace Whizbang.Sagas;

/// <summary>
/// Caller-supplied identity bundle passed to every <c>BaseSagaService</c>
/// operation. Carries the saga's stream id, the underlying domain
/// entity id, and an optional account / user id for audit propagation.
/// </summary>
/// <remarks>
/// <para>
/// <c>SagaId</c> is the framework-level identity — the stream id of the
/// saga's projection. <c>EntityId</c> is the consumer-domain identity
/// (e.g. tenant id, operation id) carried on every saga event for
/// consumer-side filtering and notification routing.
/// </para>
/// <para>
/// The two ids are <em>frequently</em> the same value (when the saga
/// has a 1:1 relationship with a domain entity) but the framework does
/// not assume so — they're distinct fields so a consumer can use
/// composite identity (e.g. <c>SagaId = TrackedGuid</c>,
/// <c>EntityId = TenantId</c>).
/// </para>
/// </remarks>
public readonly record struct SagaContext(
  Guid SagaId,
  Guid EntityId,
  Guid? AccountId = null);
