using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Ephemeral;

/// <summary>
/// Unit tests for the runtime ephemeral-mode lookup that indexes the compile-time
/// <see cref="IMessageTypeCatalog"/> into a ClrTypeName -&gt; <see cref="EphemeralInfo"/> map.
/// This is the runtime seam the consumption-gated reaper (Destruction.WhenConsumed) and the
/// rebuild/rewind guards read to answer "is this event type ephemeral, and with what
/// Destruction/Storage?" — the DB registry does not carry ephemeral mode, so it is derived
/// from the generated catalog at startup.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/EphemeralModeResolver.cs</tests>
[Category("Core")]
[Category("Ephemeral")]
public class EphemeralModeResolverTests {
  private static EphemeralModeResolver _resolver(params MessageTypeCatalogEntry[] entries) =>
    new(new FakeCatalog(entries));

  private static MessageTypeCatalogEntry _ephemeral(string clr, Destruction d, TransientStorage s) =>
    new(typeof(object), clr, "event", null) { Ephemeral = new EphemeralInfo(d, s) };

  private static MessageTypeCatalogEntry _sourced(string clr, string kind = "event") =>
    new(typeof(object), clr, kind, null);

  [Test]
  public async Task Resolve_EphemeralEvent_ReturnsItsModeAsync() {
    var resolver = _resolver(_ephemeral("Ns.Presence", Destruction.WhenConsumed, TransientStorage.InMemory));

    var info = resolver.Resolve("Ns.Presence");

    await Assert.That(info).IsNotNull();
    await Assert.That(info!.Destruction).IsEqualTo(Destruction.WhenConsumed);
    await Assert.That(info.Storage).IsEqualTo(TransientStorage.InMemory);
  }

  [Test]
  public async Task Resolve_SourcedEvent_ReturnsNullAsync() {
    var resolver = _resolver(_sourced("Ns.OrderPlaced"));

    await Assert.That(resolver.Resolve("Ns.OrderPlaced")).IsNull();
  }

  [Test]
  public async Task Resolve_UnknownType_ReturnsNullAsync() {
    var resolver = _resolver(_ephemeral("Ns.Presence", Destruction.WhenConsumed, TransientStorage.InMemory));

    await Assert.That(resolver.Resolve("Ns.Nonexistent")).IsNull();
  }

  [Test]
  public async Task IsEphemeral_ReflectsCatalogAsync() {
    var resolver = _resolver(
      _ephemeral("Ns.Presence", Destruction.WhenConsumed, TransientStorage.InMemory),
      _sourced("Ns.OrderPlaced"));

    await Assert.That(resolver.IsEphemeral("Ns.Presence")).IsTrue();
    await Assert.That(resolver.IsEphemeral("Ns.OrderPlaced")).IsFalse();
    await Assert.That(resolver.IsEphemeral("Ns.Unknown")).IsFalse();
  }

  [Test]
  public async Task Resolve_MultipleEphemeralTypes_KeepsEachDistinctModeAsync() {
    var resolver = _resolver(
      _ephemeral("Ns.Presence", Destruction.WhenConsumed, TransientStorage.InMemory),
      _ephemeral("Ns.DraftEdit", Destruction.AfterTtl, TransientStorage.TtlRow));

    await Assert.That(resolver.Resolve("Ns.Presence")!.Storage).IsEqualTo(TransientStorage.InMemory);
    await Assert.That(resolver.Resolve("Ns.DraftEdit")!.Destruction).IsEqualTo(Destruction.AfterTtl);
    await Assert.That(resolver.Resolve("Ns.DraftEdit")!.Storage).IsEqualTo(TransientStorage.TtlRow);
  }

  [Test]
  public async Task Constructor_NullCatalog_ThrowsAsync() {
    await Assert.That(() => new EphemeralModeResolver(null!)).Throws<ArgumentNullException>();
  }

  private sealed class FakeCatalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
