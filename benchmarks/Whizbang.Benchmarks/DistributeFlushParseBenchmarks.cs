// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// The cost of one flush's five Distribute stages over its messages (#1526, plans/message-cache.md step 1).
/// </summary>
/// <remarks>
/// <para>
/// A flush runs PreDistribute (detached and inline), Distribute (detached) and PostDistribute (detached and inline),
/// exactly as <c>WorkCoordinatorFlushHelper</c> does, and waits for the detached stages. Every message carries a payload
/// of about 1 KB, read through a source-generated JSON context as the registered lifecycle deserializer reads it. The
/// receptor invoker does nothing, so the numbers are the stages' own cost: scopes, envelopes and payload parses.
/// </para>
/// <para>
/// Run it on the commit before the change and on the change itself to compare: before, each stage parsed every payload
/// again (five parses per message); after, the stages of one flush share one parse.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MarkdownExporter]
public partial class DistributeFlushParseBenchmarks {
  [Params(1, 64)]
  public int Messages { get; set; }

  private ServiceProvider _provider = null!;
  private IServiceScopeFactory _scopeFactory = null!;
  private readonly BenchmarkPayloadDeserializer _deserializer = new();
  private OutboxMessage[] _outbox = [];

  [GlobalSetup]
  public void Setup() {
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker, NoOpReceptorInvoker>();
    _provider = services.BuildServiceProvider();
    _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    _outbox = [.. Enumerable.Range(0, Messages).Select(_outboxMessage)];
  }

  [GlobalCleanup]
  public void Cleanup() => _provider.Dispose();

  [Benchmark]
  public async Task FiveDistributeStagesAsync() {
    var detached = new DetachedLifecycleWork();
    var context = new DistributeLifecycleContext(_outbox, [], _scopeFactory, _deserializer, Logger: null,
      EnableLifecycleTracing: false, DetachedWork: detached);
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PreDistributeDetached, LifecycleStage.PreDistributeInline, context);
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(LifecycleStage.DistributeDetached, context);
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);
    await detached.DrainAsync(CancellationToken.None);
  }

  private static OutboxMessage _outboxMessage(int index) {
    var messageId = MessageId.New();
    var payload = new BenchmarkOrderPlaced(
      Guid.CreateVersion7(),
      $"customer-{index}",
      [.. Enumerable.Range(0, 8).Select(line => new BenchmarkOrderLine($"sku-{index}-{line}", "A product description long enough to look like one", line + 1, 19.99m * (line + 1)))],
      "1 Example Street, Example City, EX 00000",
      "Leave at the front desk");
    var element = JsonSerializer.SerializeToElement(payload, BenchmarkPayloadContext.Default.BenchmarkOrderPlaced);
    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "benchmark-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        Payload = element,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[BenchmarkOrderPlaced, Whizbang.Benchmarks]]",
      MessageType = "BenchmarkOrderPlaced, Whizbang.Benchmarks"
    };
  }

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  /// <summary>Reads every payload through the source-generated context, as the registered deserializer does.</summary>
  private sealed class BenchmarkPayloadDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => _read(envelope.Payload);
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => _read(envelope.Payload);
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      JsonSerializer.Deserialize(jsonBytes, BenchmarkPayloadContext.Default.BenchmarkOrderPlaced)!;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => _read(jsonElement);

    private static BenchmarkOrderPlaced _read(JsonElement element) =>
      element.Deserialize(BenchmarkPayloadContext.Default.BenchmarkOrderPlaced)!;
  }
}

/// <summary>A payload of about 1 KB for <see cref="DistributeFlushParseBenchmarks"/>.</summary>
public sealed record BenchmarkOrderPlaced(Guid OrderId, string CustomerId, BenchmarkOrderLine[] Lines, string ShipTo, string Notes);

/// <summary>One line of <see cref="BenchmarkOrderPlaced"/>.</summary>
public sealed record BenchmarkOrderLine(string Sku, string Description, int Quantity, decimal Price);

[JsonSerializable(typeof(BenchmarkOrderPlaced))]
internal sealed partial class BenchmarkPayloadContext : JsonSerializerContext;
