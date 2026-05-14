using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 22a of plans/slice-22-source-gen-atomic-upsert.md. Locks the runtime contract of
/// the per-TModel atomic-upsert registry: source generators register typed strategies at
/// module init, callers look them up by Type, the registry is concurrency-safe.
/// </summary>
public class PerspectiveAtomicUpsertRegistryTests {

  [Test]
  public async Task TryGet_UnregisteredType_ReturnsFalseAsync() {
    // Fresh type that nothing has registered yet → TryGet must return false so callers
    // can fall back to the legacy SELECT-then-INSERT/UPDATE path.
    var found = PerspectiveAtomicUpsertRegistry.TryGet(typeof(_UnregisteredFixture), out var strategy);
    await Assert.That(found).IsFalse();
    await Assert.That(strategy).IsNull();
  }

  [Test]
  public async Task Register_ThenTryGet_ReturnsRegisteredStrategyAsync() {
    var modelType = typeof(_RegisterFixture);
    var stub = new _StubStrategy();

    PerspectiveAtomicUpsertRegistry.Register(modelType, stub);

    var found = PerspectiveAtomicUpsertRegistry.TryGet(modelType, out var strategy);
    await Assert.That(found).IsTrue();
    await Assert.That(strategy).IsSameReferenceAs(stub);
  }

  [Test]
  public async Task Register_SameTypeTwice_ReplacesPreviousStrategyAsync() {
    // Source generator may emit the registration via [ModuleInitializer] more than once
    // if a consumer assembly references multiple Whizbang.Data.EFCore.Postgres versions
    // during a transitional pack. The registry must accept the latest registration without
    // throwing — last-write-wins.
    var modelType = typeof(_ReplaceFixture);
    var first = new _StubStrategy();
    var second = new _StubStrategy();

    PerspectiveAtomicUpsertRegistry.Register(modelType, first);
    PerspectiveAtomicUpsertRegistry.Register(modelType, second);

    var found = PerspectiveAtomicUpsertRegistry.TryGet(modelType, out var strategy);
    await Assert.That(found).IsTrue();
    await Assert.That(strategy).IsSameReferenceAs(second);
  }

  [Test]
  public async Task Register_NullStrategy_ThrowsAsync() {
    // Defensive: a null registration would cause a NullRef downstream during the fast-path
    // dispatch. Fail loud at registration time.
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
      Task.Run(() => PerspectiveAtomicUpsertRegistry.Register(typeof(_NullFixture), null!)));
  }

  [Test]
  public async Task Register_NullModelType_ThrowsAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
      Task.Run(() => PerspectiveAtomicUpsertRegistry.Register(null!, new _StubStrategy())));
  }

  [Test]
  public async Task TryGet_NullModelType_ReturnsFalseAsync() {
    var found = PerspectiveAtomicUpsertRegistry.TryGet(null!, out var strategy);
    await Assert.That(found).IsFalse();
    await Assert.That(strategy).IsNull();
  }

  // Fixture types: marker classes only — registry keys on Type.
  private sealed class _UnregisteredFixture { }
  private sealed class _RegisterFixture { }
  private sealed class _ReplaceFixture { }
  private sealed class _NullFixture { }

  private sealed class _StubStrategy : IPerspectiveAtomicUpsertStrategy {
    public Task UpsertAsync(
        DbContext context,
        Guid id,
        object model,
        PerspectiveMetadata metadata,
        PerspectiveScope scope,
        bool forceUpdateScope,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
