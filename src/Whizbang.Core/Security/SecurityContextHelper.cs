using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Security;

/// <summary>
/// Helper methods for establishing security context in message processing pipelines.
/// Consolidates duplicate code from ReceptorInvoker, PerspectiveWorker, ServiceBusConsumerWorker, and Dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// This helper provides consistent security context establishment across all message processing paths:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="EstablishScopeContextAsync"/>: Sets IScopeContextAccessor.Current from envelope</description></item>
/// <item><description><see cref="SetMessageContextFromEnvelope"/>: Sets IMessageContextAccessor.Current from envelope</description></item>
/// <item><description><see cref="EstablishFullContextAsync"/>: Does both operations for complete context establishment</description></item>
/// <item><description><see cref="EstablishMessageContextForCascade"/>: For cascade paths where no envelope is available</description></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/message-security#security-context-helper</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs</tests>
public static class SecurityContextHelper {
  /// <summary>
  /// Establishes security context from envelope using IMessageSecurityContextProvider.
  /// Sets IScopeContextAccessor.Current with the established context.
  /// </summary>
  /// <param name="envelope">Message envelope with security metadata in hops</param>
  /// <param name="scopedProvider">Scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The established scope context, or null if no context could be established</returns>
  /// <remarks>
  /// <para>
  /// Use this when you only need IScopeContextAccessor set (e.g., ServiceBusConsumerWorker)
  /// but don't need IMessageContextAccessor.
  /// </para>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishScopeContextAsync_WithProvider_SetsAccessorCurrentAsync</tests>
  public static async ValueTask<IScopeContext?> EstablishScopeContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);

    var securityProvider = scopedProvider.GetService<IMessageSecurityContextProvider>();
    if (securityProvider is null) {
      return null;
    }

    var securityContext = await securityProvider
      .EstablishContextAsync(envelope, scopedProvider, cancellationToken)
      .ConfigureAwait(false);

    if (securityContext is not null) {
      var accessor = scopedProvider.GetService<IScopeContextAccessor>();
      if (accessor is not null) {
        accessor.Current = securityContext;
      }
    }

    return securityContext;
  }

  /// <summary>
  /// Sets IMessageContextAccessor.Current from envelope.
  /// Extracts UserId from envelope's security context (last hop).
  /// </summary>
  /// <param name="envelope">Message envelope</param>
  /// <param name="scopedProvider">Scoped service provider</param>
  /// <remarks>
  /// <para>
  /// This reads the security context from the envelope's hops and sets the message context
  /// with MessageId, CorrelationId, CausationId, Timestamp, and UserId.
  /// </para>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:SetMessageContextFromEnvelope_WithSecurityContext_SetsUserIdAsync</tests>
  public static void SetMessageContextFromEnvelope(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);

    var messageContextAccessor = scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is null) {
      return;
    }

    var securityContext = envelope.GetCurrentSecurityContext();
    messageContextAccessor.Current = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = envelope.GetCorrelationId() ?? CorrelationId.New(),
      CausationId = envelope.GetCausationId() ?? MessageId.New(),
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = securityContext?.UserId
    };
  }

  /// <summary>
  /// Full security context establishment: scope context + message context.
  /// Use this from workers processing incoming messages.
  /// </summary>
  /// <param name="envelope">Message envelope with security metadata</param>
  /// <param name="scopedProvider">Scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// This is the standard method for establishing complete security context when processing
  /// incoming messages. It:
  /// </para>
  /// <list type="number">
  /// <item><description>Calls <see cref="EstablishScopeContextAsync"/> to set IScopeContextAccessor.Current</description></item>
  /// <item><description>Calls <see cref="SetMessageContextFromEnvelope"/> to set IMessageContextAccessor.Current</description></item>
  /// </list>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishFullContextAsync_SetsBothContextsAsync</tests>
  public static async ValueTask EstablishFullContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
    await EstablishScopeContextAsync(envelope, scopedProvider, cancellationToken).ConfigureAwait(false);
    SetMessageContextFromEnvelope(envelope, scopedProvider);
  }

  /// <summary>
  /// Establishes message context for cascaded receptor invocation.
  /// Reads UserId from ScopeContextAccessor and sets MessageContextAccessor.CurrentContext.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Used by Dispatcher cascade path where no envelope is available. The cascade path
  /// invokes receptors via raw delegates, bypassing the normal ReceptorInvoker flow.
  /// </para>
  /// <para>
  /// This method reads the UserId from the current scope context (set by the parent receptor's
  /// invocation via ReceptorInvoker) and establishes a new message context with that UserId.
  /// </para>
  /// <para>
  /// Uses static accessors (<see cref="ScopeContextAccessor.CurrentContext"/> and
  /// <see cref="MessageContextAccessor.CurrentContext"/>) because the Dispatcher is a singleton
  /// and cannot resolve scoped services.
  /// </para>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishMessageContextForCascade_WithScopeContext_PropagatesUserIdAsync</tests>
  public static void EstablishMessageContextForCascade() {
    string? userId = null;
    if (ScopeContextAccessor.CurrentContext is ImmutableScopeContext ctx) {
      userId = ctx.Scope.UserId;
    }

    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = userId
    };
  }
}
