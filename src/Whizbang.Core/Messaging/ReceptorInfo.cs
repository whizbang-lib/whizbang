using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Parameters: (service provider for scoped resolution, message object, cancellation token)
/// Returns: The receptor's return value (null for void receptors, IEvent for event-producing receptors).
/// The service provider should be from a scope created by the invoker.
/// </param>
/// <param name="SyncAttributes">
/// Optional list of perspective sync attributes from the receptor class.
/// When present, the invoker will await perspective sync before invoking the receptor.
/// </param>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed record ReceptorInfo(
    Type MessageType,
    string ReceptorId,
    Func<IServiceProvider, object, CancellationToken, ValueTask<object?>> InvokeAsync,
    IReadOnlyList<ReceptorSyncAttributeInfo>? SyncAttributes = null
);

/// <summary>
/// Contains the extracted data from an <see cref="AwaitPerspectiveSyncAttribute"/> on a receptor.
/// </summary>
/// <remarks>
/// This record stores the attribute data in a form suitable for runtime use.
/// The <see cref="ToSyncOptions"/> method creates the actual sync options.
/// </remarks>
/// <param name="PerspectiveType">The type of perspective to wait for.</param>
/// <param name="EventTypes">Optional event types to filter. Null means all events.</param>
/// <param name="LookupMode">The sync lookup mode (Local or Distributed).</param>
/// <param name="TimeoutMs">The timeout in milliseconds.</param>
/// <param name="ThrowOnTimeout">Whether to throw on timeout.</param>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public sealed record ReceptorSyncAttributeInfo(
    Type PerspectiveType,
    IReadOnlyList<Type>? EventTypes,
    SyncLookupMode LookupMode,
    int TimeoutMs,
    bool ThrowOnTimeout
) {
  /// <summary>
  /// Converts this attribute info to <see cref="PerspectiveSyncOptions"/>.
  /// </summary>
  /// <returns>The sync options configured from this attribute data.</returns>
  public PerspectiveSyncOptions ToSyncOptions() {
    SyncFilterNode filter = EventTypes is { Count: > 0 }
        ? new EventTypeFilter(EventTypes)
        : new AllPendingFilter();

    return new PerspectiveSyncOptions {
      Filter = filter,
      LookupMode = LookupMode,
      Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
      DebuggerAwareTimeout = true
    };
  }
}
