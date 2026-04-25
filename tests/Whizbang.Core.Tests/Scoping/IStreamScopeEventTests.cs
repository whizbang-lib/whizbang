using Whizbang.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for <see cref="IStreamScopeEvent"/>. The marker inherits from
/// <see cref="IScopeEvent"/> so every IStreamScopeEvent implementation is also an
/// IScopeEvent — the type system enforces the "stronger fan-out marker" relationship.
/// </summary>
/// <tests>IStreamScopeEvent</tests>
public class IStreamScopeEventTests {
  private sealed class StreamScopeChangeEvent : IStreamScopeEvent, IEvent {
    public Guid MessageId { get; init; }
    public Guid StreamId { get; init; }
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
    public PerspectiveScope Scope { get; init; } = new();
  }

  [Test]
  public async Task IStreamScopeEvent_InheritsIScopeEventAsync() {
    var evt = new StreamScopeChangeEvent();
    var asScopeEvent = evt as IScopeEvent;
    await Assert.That(asScopeEvent).IsNotNull();
  }

  [Test]
  public async Task IStreamScopeEvent_ScopePropertyAccessibleAsync() {
    var scope = new PerspectiveScope { TenantId = "t-1", UserId = "u-1" };
    var evt = new StreamScopeChangeEvent { Scope = scope };
    var got = ((IScopeEvent)evt).Scope;
    await Assert.That(got.TenantId).IsEqualTo("t-1");
    await Assert.That(got.UserId).IsEqualTo("u-1");
  }

  [Test]
  public async Task IStreamScopeEvent_TypeCheckOnEventInstanceAsync() {
    IEvent evt = new StreamScopeChangeEvent();
    var isStreamScope = evt is IStreamScopeEvent;
    var isScope = evt is IScopeEvent;
    await Assert.That(isStreamScope).IsTrue();
    await Assert.That(isScope).IsTrue();
  }

  [Test]
  public async Task IScopeEvent_NotAlsoIStreamScopeEvent_ByDefaultAsync() {
    // Sanity: a plain IScopeEvent shouldn't satisfy IStreamScopeEvent — the relationship
    // is one-way (every IStreamScopeEvent is an IScopeEvent, but not the reverse).
    object plain = new PlainScopeEvent();
    var isStreamScope = plain is IStreamScopeEvent;
    var isScope = plain is IScopeEvent;
    await Assert.That(isStreamScope).IsFalse();
    await Assert.That(isScope).IsTrue();
  }

  private sealed class PlainScopeEvent : IScopeEvent, IEvent {
    public Guid MessageId { get; init; }
    public Guid StreamId { get; init; }
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
    public PerspectiveScope Scope { get; init; } = new();
  }
}
