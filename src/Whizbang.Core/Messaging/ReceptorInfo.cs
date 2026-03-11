using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Contains metadata about a receptor for AOT-compatible invocation.
/// Used by <see cref="IReceptorRegistry"/> to provide compile-time discovered receptor information.
/// </summary>
/// <remarks>
/// The source generator categorizes receptors at compile time, so there's no need to store
/// FireAtStages here - the registry lookup already includes the stage.
/// </remarks>
/// <param name="MessageType">The message type this receptor handles.</param>
/// <param name="ReceptorId">Unique identifier for this receptor (typically the type name).</param>
/// <param name="InvokeAsync">
/// Pre-compiled delegate for AOT-compatible invocation.
/// Parameters: (service provider for scoped resolution, message object, source envelope for debugging/logging, caller info from dispatch site, cancellation token)
/// Returns: The receptor's return value (null for void receptors, IEvent for event-producing receptors).
/// The service provider should be from a scope created by the invoker.
/// </param>
/// <param name="SyncAttributes">
/// Optional list of perspective sync attributes from the receptor class.
/// When present, the invoker will await perspective sync before invoking the receptor.
/// </param>
/// <param name="CallerInfo">
/// Optional caller info captured from the dispatch site.
/// Populated by the invoker from the envelope's first Current hop before invocation.
/// </param>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed record ReceptorInfo(
    Type MessageType,
    string ReceptorId,
    Func<IServiceProvider, object, IMessageEnvelope, ICallerInfo?, CancellationToken, ValueTask<object?>> InvokeAsync,
    IReadOnlyList<ReceptorSyncAttributeInfo>? SyncAttributes = null,
    ICallerInfo? CallerInfo = null
);

/// <summary>
/// Contains the extracted data from an <see cref="AwaitPerspectiveSyncAttribute"/> on a receptor.
/// </summary>
/// <remarks>
/// This record stores the attribute data in a form suitable for runtime use.
/// </remarks>
/// <param name="PerspectiveType">The type of perspective to wait for.</param>
/// <param name="EventTypes">Optional event types to filter. Null means all events.</param>
/// <param name="TimeoutMs">The raw timeout in milliseconds. Use -1 for default.</param>
/// <param name="FireBehavior">The behavior when sync completes or times out.</param>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public sealed record ReceptorSyncAttributeInfo(
    Type PerspectiveType,
    IReadOnlyList<Type>? EventTypes,
    int TimeoutMs,
    SyncFireBehavior FireBehavior
) {
  /// <summary>
  /// Gets the effective timeout in milliseconds that will be used for sync.
  /// </summary>
  /// <remarks>
  /// Returns <see cref="TimeoutMs"/> if explicitly set (not -1),
  /// otherwise returns <see cref="AwaitPerspectiveSyncAttribute.DefaultTimeoutMs"/>.
  /// </remarks>
  public int EffectiveTimeoutMs => TimeoutMs == -1 ? AwaitPerspectiveSyncAttribute.DefaultTimeoutMs : TimeoutMs;
}
