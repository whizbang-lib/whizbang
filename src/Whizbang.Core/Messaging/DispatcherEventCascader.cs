using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
using Whizbang.Core.Observability;

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
/// <docs>fundamentals/receptors/lifecycle-receptors#event-cascading</docs>
public sealed partial class DispatcherEventCascader : IEventCascader {
  private readonly IServiceProvider _serviceProvider;
  private readonly ILogger<DispatcherEventCascader>? _logger;
  private IDispatcher? _dispatcher;

  /// <summary>
  /// Creates a new DispatcherEventCascader.
  /// </summary>
  /// <param name="serviceProvider">The service provider to lazily resolve the dispatcher.</param>
  /// <param name="logger">Optional logger for reporting unexpected non-message return types.</param>
  public DispatcherEventCascader(IServiceProvider serviceProvider, ILogger<DispatcherEventCascader>? logger = null) {
    ArgumentNullException.ThrowIfNull(serviceProvider);
    _serviceProvider = serviceProvider;
    _logger = logger;
  }

  /// <inheritdoc/>
  public async Task CascadeFromResultAsync(object result, IMessageEnvelope? sourceEnvelope, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(result);

    // Lazily resolve dispatcher on first use (avoids circular dependency)
    _dispatcher ??= _serviceProvider.GetRequiredService<IDispatcher>();

    // Extract all messages with their routing information
    // Handles tuples, arrays, Route wrappers, and [DefaultRouting] attributes
    foreach (var (message, mode) in MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault, _onNonMessageValue)) {
      cancellationToken.ThrowIfCancellationRequested();
      // Pass sourceEnvelope so cascaded messages can inherit SecurityContext
      await _dispatcher.CascadeMessageAsync(message, sourceEnvelope, mode, cancellationToken).ConfigureAwait(false);
    }
  }

  private void _onNonMessageValue(Type type) {
    if (_logger is not null) {
      LogNonMessageReturnType(_logger, type.FullName);
    }
  }

  [LoggerMessage(Level = LogLevel.Error, Message = "Receptor returned unexpected non-message type {TypeName} during cascade. Only IMessage, tuples, arrays, enumerables, and Routed<T> are supported.")]
  private static partial void LogNonMessageReturnType(ILogger logger, string? typeName);
}
