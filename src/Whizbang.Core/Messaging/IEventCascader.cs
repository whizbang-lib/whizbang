using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Cascades messages returned from receptor invocations.
/// When a receptor returns IMessage instances (events or commands, directly, in tuples, or arrays),
/// these messages should be dispatched to other receptors and/or published to outbox.
/// </summary>
/// <docs>core-concepts/lifecycle-receptors#event-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public interface IEventCascader {
  /// <summary>
  /// Cascades a message that was returned from a receptor invocation.
  /// This typically publishes the message via the dispatcher.
  /// </summary>
  /// <param name="message">The message (event or command) to cascade.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the async operation.</returns>
  Task CascadeAsync(IMessage message, CancellationToken cancellationToken = default);
}
