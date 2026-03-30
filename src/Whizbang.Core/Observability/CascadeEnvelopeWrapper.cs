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
internal sealed class CascadeEnvelopeWrapper : IMessageEnvelope {
  private readonly IMessageEnvelope _inner;

  public CascadeEnvelopeWrapper(IMessageEnvelope inner) {
    _inner = inner;
    DispatchContext = inner.DispatchContext.WithDefaultDispatch();
  }

  public int Version => _inner.Version;
  public MessageDispatchContext DispatchContext { get; }
  public MessageId MessageId => _inner.MessageId;
  public object Payload => _inner.Payload;
  public List<MessageHop> Hops => _inner.Hops;
  public void AddHop(MessageHop hop) => _inner.AddHop(hop);
  public DateTimeOffset GetMessageTimestamp() => _inner.GetMessageTimestamp();
  public CorrelationId? GetCorrelationId() => _inner.GetCorrelationId();
  public MessageId? GetCausationId() => _inner.GetCausationId();
  public JsonElement? GetMetadata(string key) => _inner.GetMetadata(key);
  public ScopeContext? GetCurrentScope() => _inner.GetCurrentScope();

#pragma warning disable CS0618 // Obsolete GetCurrentSecurityContext
  public SecurityContext? GetCurrentSecurityContext() => _inner.GetCurrentSecurityContext();
#pragma warning restore CS0618
}
