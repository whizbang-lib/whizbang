using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Cascades messages returned from receptor invocations.
/// When a receptor returns IMessage instances (events or commands, directly, in tuples, or arrays),
/// these messages should be dispatched to other receptors and/or published to outbox.
/// Supports routing via Route.Local(), Route.Outbox(), Route.Both() wrappers.
/// </summary>
/// <docs>core-concepts/lifecycle-receptors#event-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public interface IEventCascader {
  /// <summary>
  /// Cascades all messages from a receptor's return value.
  /// Extracts messages from tuples, arrays, and Route wrappers.
  /// Applies routing based on wrapper type and [DefaultRouting] attributes.
  /// </summary>
  /// <param name="result">The receptor's return value (may be single message, tuple, array, or Route wrapper).</param>
  /// <param name="receptorDefault">Optional default routing from receptor's [DefaultRouting] attribute.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the async operation.</returns>
  /// <remarks>
  /// <para>
  /// Routing priority (highest to lowest):
  /// 1. Message's [DefaultRouting] attribute
  /// 2. Route.Local()/Route.Outbox()/Route.Both() wrapper
  /// 3. Receptor's [DefaultRouting] attribute (receptorDefault parameter)
  /// 4. System default: Outbox
  /// </para>
  /// </remarks>
  Task CascadeFromResultAsync(object result, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default);
}
