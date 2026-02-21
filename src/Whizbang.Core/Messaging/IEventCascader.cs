using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;

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
  /// <param name="sourceEnvelope">
  /// The source envelope that caused this cascade (e.g., the command envelope).
  /// Used to inherit SecurityContext for cascaded messages when ambient context is unavailable.
  /// </param>
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
  /// <para>
  /// Security context inheritance: Each cascaded message gets its own new envelope.
  /// The SecurityContext in the new envelope's initial hop is inherited from the
  /// sourceEnvelope's current security context when ambient context is unavailable.
  /// </para>
  /// </remarks>
  Task CascadeFromResultAsync(object result, IMessageEnvelope? sourceEnvelope, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default);
}
