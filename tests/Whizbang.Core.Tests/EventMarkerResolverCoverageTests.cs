using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests;

/// <summary>
/// Covers <see cref="EventMarkerResolver.Resolve(Type)"/> — the typed-dispatch counterpart of the
/// wire-name lookup covered elsewhere (<c>MintedTypeRenameCompatibilityTests</c>). No production
/// call site in this codebase uses it today (the receive-path derivation in
/// <see cref="EventFlagsDeriver"/> always goes through the string overload), but it is public API
/// on <see cref="IEventMarkerResolver"/> for callers holding a CLR <see cref="Type"/> rather than a
/// wire name, so it must behave correctly on its own rather than riding along on the string path's
/// coverage.
/// </summary>
/// <code-under-test>src/Whizbang.Core/EventMarkerResolver.cs</code-under-test>
public class EventMarkerResolverCoverageTests {
  private sealed class _knownType;
  private sealed class _unknownType;

  private sealed class _catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(_knownType), TypeNameFormatter.FormatClrTypeName(typeof(_knownType)), "event", null) {
        IsComposite = true,
      },
    ];
  }

  [Test]
  public async Task Resolve_Type_KnownType_ReturnsCatalogStampedFlagsAsync() {
    // If this regresses, a caller on the typed dispatch path (holding a CLR Type rather than a
    // wire name) silently loses the composite/collective/compacted markers the catalog stamped
    // for that type.
    var resolver = new EventMarkerResolver(new _catalog());

    var flags = resolver.Resolve(typeof(_knownType));

    await Assert.That(flags is not null).IsTrue();
    await Assert.That(flags!.Value.HasFlag(EventFlags.Composite)).IsTrue();
  }

  [Test]
  public async Task Resolve_Type_UnknownType_ReturnsNullAsync() {
    var resolver = new EventMarkerResolver(new _catalog());

    var flags = resolver.Resolve(typeof(_unknownType));

    await Assert.That(flags).IsNull()
      .Because("a type absent from the catalog must resolve to null (unknown here), not EventFlags.None — a miss means callers fall back to runtime type checks rather than assuming no markers apply.");
  }
}
