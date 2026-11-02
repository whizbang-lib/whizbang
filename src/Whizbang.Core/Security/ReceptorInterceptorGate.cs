// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.Security.Exceptions;

namespace Whizbang.Core.Security;

/// <summary>
/// The one place a receptor invocation asks the registered <see cref="IReceptorInterceptor"/>s whether it may run. The
/// generated dispatcher (send, local invoke, publish, cascade) and the receptor invoker (inbox delivery and every other
/// lifecycle stage) both come here immediately before a receptor's handler, so no path invokes a receptor without it.
/// </summary>
/// <remarks>
/// <para>
/// An allowed invocation proceeds. A <see cref="DeniedAction.DropQuiet"/> denial is logged and the receptor is skipped
/// without an exception. Every other denial throws <see cref="ReceptorInvocationDeniedException"/> instead of running
/// the receptor; the inbox worker decides dead-letter, quarantine or retry before it completes the row.
/// </para>
/// <para>
/// Callers resolve the pipeline once with <see cref="Active"/> and hold null for an empty one, so a host that
/// registers no interceptor resolves nothing and allocates nothing per message.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorGateTests.cs</tests>
internal static partial class ReceptorInterceptorGate {
  /// <summary>
  /// The registered pipeline when it holds at least one interceptor, otherwise null. Reads the registered
  /// <see cref="ReceptorInterceptorPipeline"/>, and builds one from the registered interceptors when a container never
  /// ran the framework defaults, so an interceptor registered on its own is still enforced.
  /// </summary>
  internal static ReceptorInterceptorPipeline? Active(IServiceProvider serviceProvider) {
    var pipeline = serviceProvider.GetService<ReceptorInterceptorPipeline>();
    if (pipeline is null) {
      // GetService, not GetServices: a host's minimal provider may not resolve enumerables at all
      var interceptors = serviceProvider.GetService<IEnumerable<IReceptorInterceptor>>();
      pipeline = interceptors is null ? null : new ReceptorInterceptorPipeline(interceptors);
    }
    return pipeline is { IsEmpty: false } ? pipeline : null;
  }

  /// <summary>
  /// Asks the pipeline whether <paramref name="receptorType"/> may handle the message. True to invoke; false for a
  /// <see cref="DeniedAction.DropQuiet"/> denial; throws <see cref="ReceptorInvocationDeniedException"/> otherwise.
  /// </summary>
  internal static async ValueTask<bool> AdmitAsync(
      ReceptorInterceptorPipeline pipeline,
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {
    var result = await pipeline.EvaluateFirstDenialAsync(receptorType, envelope, context, cancellationToken).ConfigureAwait(false);
    return Decide(result, receptorType, envelope, scopedProvider);
  }

  /// <summary>
  /// <see cref="AdmitAsync"/> for a synchronous receptor, which runs on the caller's thread and cannot wait. Every
  /// interceptor must complete synchronously for it (the attribute-based default does); one that does not is refused
  /// loudly rather than blocked on.
  /// </summary>
  internal static bool Admit(
      ReceptorInterceptorPipeline pipeline,
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      IServiceProvider scopedProvider) {
    var pending = pipeline.EvaluateFirstDenialAsync(receptorType, envelope, context, CancellationToken.None);
    if (!pending.IsCompletedSuccessfully) {
      // Observe the abandoned evaluation so a later fault is not unobserved
      _ = pending.AsTask().ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
      throw new InvalidOperationException(
        $"A receptor interceptor did not complete synchronously for the synchronous receptor {TypeNameFormatter.DisplayName(receptorType)}. " +
        "A synchronous receptor runs on the caller's thread, so every interceptor that applies to it must answer synchronously.");
    }
    return Decide(pending.Result, receptorType, envelope, scopedProvider);
  }

  /// <summary>
  /// What a receptor invocation does with the pipeline's answer: true to run, false to skip quietly, or the denial
  /// exception for every action that must not pass silently.
  /// </summary>
  internal static bool Decide(InterceptorResult result, Type receptorType, IMessageEnvelope envelope, IServiceProvider scopedProvider) {
    if (result.Allow) {
      return true;
    }
    if (result.OnDenied == DeniedAction.DropQuiet) {
      if (scopedProvider.GetService<ILogger<ReceptorInterceptorPipeline>>() is { } logger) {
        LogDroppedQuietly(logger, TypeNameFormatter.DisplayName(receptorType), envelope.MessageId.Value, result.Reason ?? "none given");
      }
      return false;
    }
    throw new ReceptorInvocationDeniedException(receptorType, result.OnDenied, result.Reason);
  }

  /// <summary>
  /// The scope context a receptor reads through <see cref="IScopeContextAccessor.Current"/> at this point of its
  /// invocation, which is what its interceptors are shown.
  /// </summary>
  internal static IScopeContext? ReceptorScopeContext(IServiceProvider scopedProvider) =>
    scopedProvider.GetService<IScopeContextAccessor>()?.Current ?? ScopeContextAccessor.CurrentContext;

  [LoggerMessage(
    EventId = 1996,
    Level = LogLevel.Warning,
    Message = "Receptor {ReceptorType} was skipped for message {MessageId}: a receptor interceptor denied it with DropQuiet. Reason: {Reason}")]
  private static partial void LogDroppedQuietly(ILogger logger, string receptorType, Guid messageId, string reason);
}
