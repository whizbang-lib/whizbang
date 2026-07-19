using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Ephemeral;

/// <summary>
/// Unit tests for the dispatch-time derivation of <see cref="EventFlags.Ephemeral"/> from a payload.
/// The deriver mirrors how <c>Composite</c>/<c>Collective</c> are derived, but ephemeral is composable
/// (not just the <see cref="IEphemeralEvent"/> marker), so it consults the <see cref="IEphemeralModeResolver"/>
/// too — falling back to the marker interface when no resolver is wired.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/Messaging/EphemeralFlagDeriver.cs</tests>
[Category("Core")]
[Category("Ephemeral")]
public class EphemeralFlagDeriverTests {
  private sealed record MarkerEvent : IEphemeralEvent;
  private sealed record ComposedEphemeralEvent;   // ephemeral only via the resolver (e.g. [Ephemeral] on a base)
  private sealed record SourcedEvent;
  private sealed record CompactedMarkerEvent : ICompactedEvent;

  private sealed class StubResolver(params Type[] ephemeralTypes) : IEphemeralModeResolver {
    private readonly HashSet<Type> _ephemeral = [.. ephemeralTypes];
    public EphemeralInfo? Resolve(string clrTypeName) => null;
    public bool IsEphemeral(string clrTypeName) => false;
    public EphemeralInfo? Resolve(Type type) =>
      _ephemeral.Contains(type) ? new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.InMemory) : null;
    public bool IsEphemeral(Type type) => _ephemeral.Contains(type);
  }

  [Test]
  public async Task Derive_MarkerEvent_NoResolver_IsEphemeralAsync() {
    var flag = EphemeralFlagDeriver.Derive(new MarkerEvent(), resolver: null);
    await Assert.That(flag).IsEqualTo(EventFlags.Ephemeral);
  }

  [Test]
  public async Task Derive_ComposedEphemeral_ViaResolver_IsEphemeralAsync() {
    var resolver = new StubResolver(typeof(ComposedEphemeralEvent));
    var flag = EphemeralFlagDeriver.Derive(new ComposedEphemeralEvent(), resolver);
    await Assert.That(flag).IsEqualTo(EventFlags.Ephemeral);
  }

  [Test]
  public async Task Derive_SourcedEvent_IsNoneAsync() {
    var resolver = new StubResolver(typeof(ComposedEphemeralEvent));
    var flag = EphemeralFlagDeriver.Derive(new SourcedEvent(), resolver);
    await Assert.That(flag).IsEqualTo(EventFlags.None);
  }

  [Test]
  public async Task Derive_ComposedEphemeral_NoResolver_FallsBackToNoneAsync() {
    // Without a wired resolver, a composed-ephemeral type (no marker) is undetectable -> None (safe default).
    var flag = EphemeralFlagDeriver.Derive(new ComposedEphemeralEvent(), resolver: null);
    await Assert.That(flag).IsEqualTo(EventFlags.None);
  }

  [Test]
  public async Task Derive_NullPayload_IsNoneAsync() {
    var flag = EphemeralFlagDeriver.Derive(null, resolver: null);
    await Assert.That(flag).IsEqualTo(EventFlags.None);
  }

  [Test]
  public async Task Derive_CompactedMarker_IsCompactedNotEphemeralAsync() {
    // E3: a compacted carry-forward is StateBased + PERMANENT. It must flag Compacted (16), never Ephemeral
    // (8) — otherwise the reaper (self-destruct = flags&8) would delete the authoritative origin.
    var flag = EphemeralFlagDeriver.Derive(new CompactedMarkerEvent(), resolver: null);
    await Assert.That(flag).IsEqualTo(EventFlags.Compacted)
      .Because("A compacted origin is permanent StateBased — flagged Compacted, so the reaper never touches it.");
  }

  [Test]
  public async Task Derive_RealCompactedEvent_IsCompactedAsync() {
    var compacted = new Whizbang.Core.Perspectives.Compacted {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "P",
      Model = default,
      SchemaVersion = 1,
      ThroughVersion = 5,
    };
    var flag = EphemeralFlagDeriver.Derive(compacted, resolver: null);
    await Assert.That(flag).IsEqualTo(EventFlags.Compacted);
  }
}
