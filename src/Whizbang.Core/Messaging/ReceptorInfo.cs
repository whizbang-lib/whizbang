using System;
using System.Threading;
using System.Threading.Tasks;

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
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed record ReceptorInfo(
    Type MessageType,
    string ReceptorId,
    Func<IServiceProvider, object, CancellationToken, ValueTask<object?>> InvokeAsync
);
