#pragma warning disable CA1707

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Locks the chain's contract: hooks run in Order ascending, each hook
/// sees the prior hook's output, replacements propagate, and metadata
/// merges into the final outcome.
/// </summary>
/// <docs>fundamentals/offloads/post-serialize-hooks</docs>
public class PostSerializeHookChainTests {

  [Test]
  public async Task RunAsync_NoHooks_ReturnsInputUnchangedAsync() {
    var chain = new PostSerializeHookChain([]);
    var ctx = _buildContext("hello"u8.ToArray());

    var outcome = await chain.RunAsync(ctx, CancellationToken.None);

    await Assert.That(outcome.FinalSerializedBytes.ToArray()).IsEquivalentTo("hello"u8.ToArray());
    await Assert.That(outcome.FinalEnvelopeType).IsEqualTo(ctx.EnvelopeType);
    await Assert.That(outcome.MergedDestinationMetadata).IsNull()
      .Because("Input destination metadata was null; no hook added anything; outcome must report null (not an empty dict that callers have to guard against).");
  }

  [Test]
  public async Task RunAsync_HooksRunInOrderAsync() {
    var visits = new List<int>();
    var chain = new PostSerializeHookChain([
      new _testHook(order: 2000, onRun: ctx => { visits.Add(2000); return PostSerializeResult.PassThrough(); }),
      new _testHook(order: 100, onRun: ctx => { visits.Add(100); return PostSerializeResult.PassThrough(); }),
      new _testHook(order: 1000, onRun: ctx => { visits.Add(1000); return PostSerializeResult.PassThrough(); }),
    ]);
    var ctx = _buildContext("hello"u8.ToArray());

    await chain.RunAsync(ctx, CancellationToken.None);

    var expectedOrder = new[] { 100, 1000, 2000 };
    await Assert.That(visits).IsEquivalentTo(expectedOrder);
  }

  [Test]
  public async Task RunAsync_BytesReplacement_NextHookSeesReplacementAsync() {
    ReadOnlyMemory<byte> bytesSeenBySecondHook = default;
    var chain = new PostSerializeHookChain([
      new _testHook(order: 100, onRun: _ => new PostSerializeResult {
        NewSerializedBytes = "REPLACED"u8.ToArray()
      }),
      new _testHook(order: 200, onRun: ctx => { bytesSeenBySecondHook = ctx.SerializedBytes; return PostSerializeResult.PassThrough(); }),
    ]);
    var ctx = _buildContext("original"u8.ToArray());

    var outcome = await chain.RunAsync(ctx, CancellationToken.None);

    await Assert.That(bytesSeenBySecondHook.ToArray()).IsEquivalentTo("REPLACED"u8.ToArray());
    await Assert.That(outcome.FinalSerializedBytes.ToArray()).IsEquivalentTo("REPLACED"u8.ToArray());
  }

  [Test]
  public async Task RunAsync_MetadataMerges_AndOverridesEarlierKeysAsync() {
    var sizeHook = new _testHook(order: 100, onRun: ctx => new PostSerializeResult {
      AdditionalDestinationMetadata = new Dictionary<string, JsonElement> {
        ["whizbang.body-size"] = JsonDocument.Parse("8").RootElement,
      }
    });
    var offloadHook = new _testHook(order: 1000, onRun: ctx => new PostSerializeResult {
      NewSerializedBytes = "CLAIM"u8.ToArray(),
      AdditionalDestinationMetadata = new Dictionary<string, JsonElement> {
        ["whizbang.body-size"] = JsonDocument.Parse("5").RootElement,    // override sizeHook's value
        ["whizbang.is-claim"] = JsonDocument.Parse("true").RootElement,
      }
    });
    var chain = new PostSerializeHookChain([sizeHook, offloadHook]);
    var ctx = _buildContext("original"u8.ToArray());

    var outcome = await chain.RunAsync(ctx, CancellationToken.None);

    await Assert.That(outcome.MergedDestinationMetadata).IsNotNull();
    await Assert.That(outcome.MergedDestinationMetadata!["whizbang.body-size"].GetInt32()).IsEqualTo(5)
      .Because("Later hooks override earlier hooks' metadata for the same key — gives the offload hook the final say on the on-the-wire size.");
    await Assert.That(outcome.MergedDestinationMetadata["whizbang.is-claim"].GetBoolean()).IsTrue();
  }

  [Test]
  public async Task RunAsync_RespectsCancellationBetweenHooksAsync() {
    using var cts = new CancellationTokenSource();
    var first = new _testHook(order: 100, onRun: _ => {
      cts.Cancel();
      return PostSerializeResult.PassThrough();
    });
    var second = new _testHook(order: 200, onRun: _ => {
      throw new InvalidOperationException("second hook must not run after cancellation");
    });
    var chain = new PostSerializeHookChain([first, second]);
    var ctx = _buildContext("hello"u8.ToArray());

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await chain.RunAsync(ctx, cts.Token));
  }

  [Test]
  public async Task IsEmpty_NoHooks_ReturnsTrueAsync() {
    var chain = new PostSerializeHookChain([]);
    await Assert.That(chain.IsEmpty).IsTrue()
      .Because("Empty chain lets the publish strategy skip the serialize-for-measurement step when no hooks need it.");
  }

  [Test]
  public async Task IsEmpty_WithHooks_ReturnsFalseAsync() {
    var chain = new PostSerializeHookChain([new _testHook(order: 1, onRun: _ => PostSerializeResult.PassThrough())]);
    await Assert.That(chain.IsEmpty).IsFalse();
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static PostSerializeContext _buildContext(byte[] bytes) {
    var envelope = new MessageEnvelope<_testPayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _testPayload("x"),
      Hops = [
        new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }
      ]
    };
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    return new PostSerializeContext(
      Envelope: envelope,
      EnvelopeType: envelope.GetType().AssemblyQualifiedName!,
      SerializedBytes: bytes,
      ContentType: "application/json",
      TransportMaxMessageSizeBytes: null,
      JsonOptions: jsonOptions,
      Destination: new TransportDestination("test")
    );
  }

  private sealed record _testPayload(string Content);

  private sealed class _testHook : IPostSerializeHook {
    private readonly Func<PostSerializeContext, PostSerializeResult> _onRun;
    public _testHook(int order, Func<PostSerializeContext, PostSerializeResult> onRun) {
      Order = order;
      _onRun = onRun;
    }
    public int Order { get; }
    public Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken) {
      return Task.FromResult(_onRun(context));
    }
  }
}
