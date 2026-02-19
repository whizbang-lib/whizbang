using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of <see cref="IEventCascader"/> that uses <see cref="IDispatcher"/>
/// to cascade messages returned from receptor invocations.
/// </summary>
/// <remarks>
/// <para>
/// This cascader extracts messages from receptor return values (including tuples, arrays,
/// and Route wrappers) and dispatches them according to their routing configuration:
/// </para>
/// <list type="bullet">
/// <item><description>Route.Local() - invokes in-process receptors only</description></item>
/// <item><description>Route.Outbox() - writes to outbox for cross-service delivery only</description></item>
/// <item><description>Route.Both() - does both local invocation and outbox write</description></item>
/// <item><description>Unwrapped messages - uses [DefaultRouting] attribute or system default (Outbox)</description></item>
/// </list>
/// <para>
/// <b>Note:</b> The dispatcher is resolved lazily to avoid circular dependency issues since
/// IDispatcher may depend on IReceptorInvoker which depends on IEventCascader.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors#event-cascading</docs>
public sealed class DispatcherEventCascader : IEventCascader {
  private readonly IServiceProvider _serviceProvider;
  private IDispatcher? _dispatcher;

  /// <summary>
  /// Creates a new DispatcherEventCascader.
  /// </summary>
  /// <param name="serviceProvider">The service provider to lazily resolve the dispatcher.</param>
  public DispatcherEventCascader(IServiceProvider serviceProvider) {
    ArgumentNullException.ThrowIfNull(serviceProvider);
    _serviceProvider = serviceProvider;
  }

  /// <inheritdoc/>
  public async Task CascadeFromResultAsync(object result, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(result);

    // Lazily resolve dispatcher on first use (avoids circular dependency)
    _dispatcher ??= _serviceProvider.GetRequiredService<IDispatcher>();

    // Extract all messages with their routing information
    // Handles tuples, arrays, Route wrappers, and [DefaultRouting] attributes
    foreach (var (message, mode) in MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      cancellationToken.ThrowIfCancellationRequested();
      await _dispatcher.CascadeMessageAsync(message, mode, cancellationToken).ConfigureAwait(false);
    }
  }
}
