using System.Text.Json;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Lightweight wrapper that delegates to a source envelope but overrides DispatchContext
/// with IsDefaultDispatch = true. Used by cascade paths to signal that only default-stage
/// receptors should fire during the cascade.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatcher#cascade-default-dispatch</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherStageFireTests.cs</tests>
internal sealed class CascadeEnvelopeWrapper(IMessageEnvelope inner) : IMessageEnvelope {
  private readonly IMessageEnvelope _inner = inner;

  public int Version => _inner.Version;
  public MessageDispatchContext DispatchContext { get; } = inner.DispatchContext.WithDefaultDispatch();
  public MessageId MessageId => _inner.MessageId;
  public object Payload => _inner.Payload;
  public List<MessageHop> Hops => _inner.Hops;
  // Delegate the per-instance treatment flags (e.g. NoRebroadcast) to the wrapped envelope — wrapping
  // for cascade must not silently drop a fan-out child's flags.
  public Messaging.EventFlags Flags => _inner.Flags;
  public List<ReceptorInvocationRecord>? ReceptorInvocations => _inner.ReceptorInvocations;
  public List<ReceptorInvocationRecord> GetOrCreateReceptorInvocations() => _inner.GetOrCreateReceptorInvocations();
  public void AddHop(MessageHop hop) => _inner.AddHop(hop);
  public DateTimeOffset GetMessageTimestamp() => _inner.GetMessageTimestamp();
  public CorrelationId? GetCorrelationId() => _inner.GetCorrelationId();
  // A child cascaded from the wrapped message is caused BY that message — its causation is the wrapped
  // envelope's own MessageId, NOT the wrapped message's causation (which is the grandparent). Mirrors
  // CascadeContextFactory.FromEnvelope (causation = envelope.MessageId).
  public MessageId? GetCausationId() => _inner.MessageId;
  public JsonElement? GetMetadata(string key) => _inner.GetMetadata(key);
  public ScopeContext? GetCurrentScope() => _inner.GetCurrentScope();

#pragma warning disable CS0618 // Obsolete GetCurrentSecurityContext
  public SecurityContext? GetCurrentSecurityContext() => _inner.GetCurrentSecurityContext();
#pragma warning restore CS0618
}
