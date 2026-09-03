using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// What an envelope that implements only the required members gets for everything else.
/// <para>
/// These defaults exist so that test doubles and envelopes written before later slices keep
/// compiling and keep working. That makes them load-bearing in a quiet way: every consumer of an
/// envelope reads them without knowing whether the implementation opted in, so the defaults have to
/// be the safe reading of "this envelope does not carry that information" rather than anything a
/// caller might mistake for real data.
/// </para>
/// <para>
/// The exception is receptor-invocation tracking, which throws instead of defaulting. Exactly-once
/// firing is decided from that list, so an envelope that silently returned an empty one would let
/// the dedup store conclude a receptor had never run and fire it again — the failure the tracking
/// exists to prevent, delivered by the fallback meant to be harmless.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/IMessageEnvelope.cs</code-under-test>
public class MessageEnvelopeDefaultsTests {

  /// <summary>An envelope implementing only what the interface requires, overriding nothing.</summary>
  private sealed class MinimalEnvelope : IMessageEnvelope {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } =
      new() { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public MessageId MessageId { get; } = MessageId.From(Guid.CreateVersion7());
    public object Payload { get; } = new();
    public List<MessageHop> Hops { get; } = [];

    // The remaining required members are not what this fixture is about; they exist so the type
    // compiles, and each returns the "carries nothing" answer.
    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => DateTimeOffset.UnixEpoch;
    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public System.Text.Json.JsonElement? GetMetadata(string key) => null;
    public ScopeContext? GetCurrentScope() => null;
#pragma warning disable CS0618 // Required by the interface; the obsolete surface still must compile.
    public SecurityContext? GetCurrentSecurityContext() => null;
#pragma warning restore CS0618
  }

  [Test]
  public async Task AnEnvelopeThatTracksNothing_ReportsAbsenceRatherThanInventedValuesAsync() {
    IMessageEnvelope envelope = new MinimalEnvelope();

    await Assert.That(envelope.ReceptorInvocations).IsNull()
      .Because("null says this envelope does not track invocations; an empty list would say it "
             + "tracks them and none have happened, which is a different claim");
    await Assert.That(envelope.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("an empty id is recognizably absent, where any real-looking value would be "
             + "attributed to a service that never sent this");
    await Assert.That(envelope.SourceCommitSequence).IsEqualTo(0L);
    await Assert.That(envelope.CausedByServiceId).IsNull();
    await Assert.That(envelope.CausedByCommitSequence).IsNull();
    await Assert.That(envelope.StateOnly).IsFalse()
      .Because("normal delivery is the safe default — defaulting to state-only would silently stop "
             + "trigger receptors firing for every envelope that did not opt out");
  }

  [Test]
  public async Task AnEnvelopeWithoutTracking_RefusesRatherThanReturningAnEmptyListAsync() {
    IMessageEnvelope envelope = new MinimalEnvelope();

    await Assert.That(() => envelope.GetOrCreateReceptorInvocations())
      .Throws<NotSupportedException>()
      .Because("returning an empty list would let the dedup store conclude a receptor had never "
             + "run and fire it again — exactly what the tracking exists to prevent");
  }

  [Test]
  public async Task TheRefusal_NamesTheOffendingEnvelopeTypeAsync() {
    // A bare NotSupportedException from a default interface member is nearly untraceable: the stack
    // points at the interface, not at whichever implementation was passed in.
    IMessageEnvelope envelope = new MinimalEnvelope();

    try {
      envelope.GetOrCreateReceptorInvocations();
      Assert.Fail("expected NotSupportedException");
    } catch (NotSupportedException ex) {
      await Assert.That(ex.Message).Contains(nameof(MinimalEnvelope))
        .Because("the message must name the type that failed to opt in, or the reader cannot tell "
               + "which envelope implementation reached here");
    }
  }
}
