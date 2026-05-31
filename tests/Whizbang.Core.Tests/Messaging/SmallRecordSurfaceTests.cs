using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Surface-locking tests for three tiny records that show 0% coverage today —
/// each is used as a data-carrier on a hot path, so we want the API shape
/// (positional ctor, value equality, with-expression copying) frozen even if
/// no production caller exercises it in unit tests.
///
/// Targets:
///   - <see cref="HandlerBatchResult"/> — per-handler commit result returned
///     from CommitHandlerBatchAsync; (HandlerId, Success, ErrorMessage).
///   - <see cref="OrderedStreamProcessorOptions"/> — the ParallelizeStreams
///     toggle used by the in-process per-stream serializer.
///   - <see cref="MessageTypeCatalogEntry"/> — source-generated registry
///     entry: (Type, ClrTypeName, Kind, PinnedId?).
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-handler-batch</docs>
public class SmallRecordSurfaceTests {

  [Test]
  public async Task HandlerBatchResult_PositionalCtor_RoundTripsValuesAsync() {
    var id = Guid.NewGuid();
    var r = new HandlerBatchResult(id, Success: true, ErrorMessage: null);

    await Assert.That(r.HandlerId).IsEqualTo(id);
    await Assert.That(r.Success).IsTrue();
    await Assert.That(r.ErrorMessage).IsNull();
  }

  [Test]
  public async Task HandlerBatchResult_FailureCarriesErrorMessageAsync() {
    var r = new HandlerBatchResult(Guid.NewGuid(), Success: false, ErrorMessage: "savepoint rolled back: unique_violation");

    await Assert.That(r.Success).IsFalse();
    await Assert.That(r.ErrorMessage).IsEqualTo("savepoint rolled back: unique_violation");
  }

  [Test]
  public async Task HandlerBatchResult_RecordValueEqualityAsync() {
    var id = Guid.NewGuid();
    var a = new HandlerBatchResult(id, true, null);
    var b = new HandlerBatchResult(id, true, null);

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task HandlerBatchResult_WithExpressionCopiesAndOverridesAsync() {
    var original = new HandlerBatchResult(Guid.NewGuid(), true, null);
    var updated = original with { Success = false, ErrorMessage = "boom" };

    await Assert.That(original.Success).IsTrue();
    await Assert.That(updated.Success).IsFalse();
    await Assert.That(updated.ErrorMessage).IsEqualTo("boom");
    await Assert.That(updated.HandlerId).IsEqualTo(original.HandlerId);
  }

  [Test]
  public async Task OrderedStreamProcessorOptions_DefaultsToSequentialAsync() {
    var opt = new OrderedStreamProcessorOptions();

    // Sequential processing is the safer default — locks the contract so a
    // future "parallel by default" flip doesn't slip in silently.
    await Assert.That(opt.ParallelizeStreams).IsFalse();
  }

  [Test]
  public async Task OrderedStreamProcessorOptions_ParallelizeStreams_RoundTripsAsync() {
    var opt = new OrderedStreamProcessorOptions { ParallelizeStreams = true };

    await Assert.That(opt.ParallelizeStreams).IsTrue();
  }

  [Test]
  public async Task MessageTypeCatalogEntry_PositionalCtor_RoundTripsValuesAsync() {
    var pinnedId = Guid.NewGuid().ToString();
    var entry = new MessageTypeCatalogEntry(
      Type: typeof(string),
      ClrTypeName: "System.String",
      Kind: "event",
      PinnedId: pinnedId);

    await Assert.That(entry.Type).IsEqualTo(typeof(string));
    await Assert.That(entry.ClrTypeName).IsEqualTo("System.String");
    await Assert.That(entry.Kind).IsEqualTo("event");
    await Assert.That(entry.PinnedId).IsEqualTo(pinnedId);
  }

  [Test]
  public async Task MessageTypeCatalogEntry_PinnedIdNull_IsValidAsync() {
    var entry = new MessageTypeCatalogEntry(
      Type: typeof(int),
      ClrTypeName: "System.Int32",
      Kind: "command",
      PinnedId: null);

    await Assert.That(entry.PinnedId).IsNull();
  }

  [Test]
  public async Task MessageTypeCatalogEntry_RecordValueEqualityAsync() {
    var a = new MessageTypeCatalogEntry(typeof(string), "s", "event", null);
    var b = new MessageTypeCatalogEntry(typeof(string), "s", "event", null);

    await Assert.That(a).IsEqualTo(b);
  }

  [Test]
  public async Task MessageTypeCatalogEntry_DifferentKind_NotEqualAsync() {
    var a = new MessageTypeCatalogEntry(typeof(string), "s", "event", null);
    var b = new MessageTypeCatalogEntry(typeof(string), "s", "perspective", null);

    await Assert.That(a).IsNotEqualTo(b);
  }
}
