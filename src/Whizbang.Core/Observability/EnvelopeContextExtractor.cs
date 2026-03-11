using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Observability;

/// <summary>
/// Extracts context (both tracing and security) from message envelopes.
/// Consolidates duplicate code from ReceptorInvoker and LifecycleInvokerTemplate.
/// </summary>
/// <remarks>
/// <para>
/// This helper provides a single source of truth for extracting:
/// </para>
/// <list type="bullet">
/// <item><description>ActivityContext (TraceParent) for OpenTelemetry trace correlation</description></item>
/// <item><description>IScopeContext (ScopeDelta) for security context propagation</description></item>
/// </list>
/// <para>
/// <strong>Usage:</strong> All workers and invokers should use this helper instead of
/// duplicating the extraction logic.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-context-extraction</docs>
/// <tests>Whizbang.Core.Tests/Observability/EnvelopeContextExtractorTests.cs</tests>
public static class EnvelopeContextExtractor {
  /// <summary>
  /// Result of extracting context from an envelope's hops.
  /// </summary>
  /// <param name="TraceContext">ActivityContext for OTel trace correlation (default if none found)</param>
  /// <param name="Scope">Security scope context (null if none found)</param>
  public readonly record struct ExtractedContext(
      ActivityContext TraceContext,
      IScopeContext? Scope);

  /// <summary>
  /// Extracts both trace context and scope from message hops.
  /// </summary>
  /// <param name="hops">Message hops containing TraceParent and ScopeDelta</param>
  /// <returns>Extracted context with both trace and scope information</returns>
  /// <remarks>
  /// <para>
  /// Trace context is extracted from the last hop's TraceParent for distributed tracing.
  /// Scope is rebuilt by merging ScopeDelta from all "Current" hops.
  /// </para>
  /// </remarks>
  public static ExtractedContext ExtractFromHops(IReadOnlyList<MessageHop>? hops) {
    if (hops == null || hops.Count == 0) {
      return new ExtractedContext(default, null);
    }

    // Extract trace context (TraceParent from last hop)
    var traceContext = ExtractTraceContext(hops);

    // Extract scope (merge ScopeDelta from all Current hops)
    var scope = ExtractScope(hops);

    return new ExtractedContext(traceContext, scope);
  }

  /// <summary>
  /// Extracts ActivityContext from message hops for trace correlation.
  /// Uses the last hop's TraceParent to link spans to the original HTTP request.
  /// </summary>
  /// <param name="hops">Message hops containing TraceParent</param>
  /// <returns>ActivityContext parsed from TraceParent, or default if none found</returns>
  public static ActivityContext ExtractTraceContext(IReadOnlyList<MessageHop>? hops) {
    if (hops == null || hops.Count == 0) {
      return default;
    }

    var traceParent = hops
        .Select(h => h.TraceParent)
        .LastOrDefault(tp => tp is not null);

    if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return parentContext;
    }

    return default;
  }

  /// <summary>
  /// Extracts scope from message hops by merging ScopeDelta from all "Current" hops.
  /// </summary>
  /// <param name="hops">Message hops containing ScopeDelta</param>
  /// <returns>Merged scope context, or null if no scope found</returns>
  public static IScopeContext? ExtractScope(IReadOnlyList<MessageHop>? hops) {
    if (hops == null || hops.Count == 0) {
      return null;
    }

    // Merge ScopeDelta from all Current hops
    ScopeContext? mergedScope = null;
    foreach (var hop in hops.Where(h => h.Type == HopType.Current && h.Scope != null)) {
      mergedScope = hop.Scope!.ApplyTo(mergedScope);
    }

    if (mergedScope == null) {
      return null;
    }

    // Wrap in ImmutableScopeContext for use
    return new ImmutableScopeContext(
        new SecurityExtraction {
          Scope = mergedScope.Scope,
          Roles = mergedScope.Roles,
          Permissions = mergedScope.Permissions,
          SecurityPrincipals = mergedScope.SecurityPrincipals,
          Claims = mergedScope.Claims,
          ActualPrincipal = mergedScope.ActualPrincipal,
          EffectivePrincipal = mergedScope.EffectivePrincipal,
          ContextType = mergedScope.ContextType,
          Source = "EnvelopeHops"
        },
        shouldPropagate: true);
  }

  /// <summary>
  /// Extracts context from an envelope directly.
  /// Convenience method that calls <see cref="ExtractFromHops"/> with envelope.Hops.
  /// </summary>
  /// <param name="envelope">Message envelope with hops</param>
  /// <returns>Extracted context with both trace and scope information</returns>
  public static ExtractedContext ExtractFromEnvelope(IMessageEnvelope envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    return ExtractFromHops(envelope.Hops);
  }
}
