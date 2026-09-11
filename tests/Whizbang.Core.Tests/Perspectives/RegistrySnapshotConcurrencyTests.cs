using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The perspective registries are filled by module initializers, which run as assemblies load, and read by startup
/// work that may already be running. Both promise a snapshot safe to enumerate while further assemblies initialize,
/// and that promise is the whole reason the read exists in a form separate from the dictionary.
///
/// A collection expression over a ConcurrentDictionary does not keep it. It reads Count, allocates, then copies, and
/// the copy throws if a registration landed in between. The failure needs no unusual load: it surfaced as two
/// unrelated reconciler tests failing in CI, because a sibling test registered a model while they enumerated.
/// </summary>
/// <docs>fundamentals/perspectives/transient-storage</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveTtlRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowCapRegistry.cs</code-under-test>
[Category("Unit")]
public class RegistrySnapshotConcurrencyTests {
  /// <summary>Distinct types to register, without reflection: closed generics over a private marker are free.</summary>
  private static Type _marker(int i) => typeof(_slot<>).MakeGenericType(i % 2 == 0 ? typeof(int) : typeof(long))
    .MakeArrayType(Math.Max(1, i % 8));

  private sealed class _slot<T> { }

  /// <summary>
  /// Registers continuously on one thread while snapshotting on another. Before the fix this throws
  /// ArgumentException from the copy; after it, every snapshot is a coherent point-in-time list.
  /// </summary>
  [Test]
  public async Task RegisteredModels_SnapshotTakenWhileRegistering_DoesNotThrowAsync() {
    using var stop = new CancellationTokenSource();
    var registrations = 0;

    var writer = Task.Run(() => {
      for (var i = 0; !stop.IsCancellationRequested && i < 20_000; i++) {
        PerspectiveTtlRegistry.Register(_marker(i), 30 + (i % 5));
        PerspectiveRowCapRegistry.Register(_marker(i + 500_000), 100 + (i % 7), "tenant");
        Interlocked.Increment(ref registrations);
      }
    });

    Exception? observed = null;
    var reader = Task.Run(() => {
      try {
        while (!writer.IsCompleted) {
          _ = PerspectiveTtlRegistry.RegisteredModels();
          _ = PerspectiveRowCapRegistry.RegisteredModels();
        }
      } catch (Exception ex) {
        observed = ex;
        stop.Cancel();
      }
    });

    await Task.WhenAll(writer, reader);

    await Assert.That(observed).IsNull()
      .Because($"the registries promise a snapshot safe to read while assemblies still initialize; snapshotting threw after {registrations} registrations: {observed?.Message}");
  }

  /// <summary>A snapshot is a copy, so a registration made after it must not appear in it.</summary>
  [Test]
  public async Task RegisteredModels_ReturnsADetachedCopy_NotALiveViewAsync() {
    var before = PerspectiveTtlRegistry.RegisteredModels();
    var countBefore = before.Count;

    PerspectiveTtlRegistry.Register(typeof(_slot<string>), 42);

    await Assert.That(before.Count).IsEqualTo(countBefore)
      .Because("a caller iterating a snapshot must not see the collection grow underneath it");
    await Assert.That(PerspectiveTtlRegistry.RegisteredModels().Count).IsGreaterThan(countBefore)
      .Because("a later snapshot does see it, which is what makes the first one a copy rather than a stale cache");
  }
}
