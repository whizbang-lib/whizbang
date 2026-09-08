using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The handler name rides on the dispatch context of the envelope an inbox worker hands the dispatcher for
/// one handler row. It exists so emissions can derive an identity that names the handler; it must not
/// change anything persisted or transported.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageDispatchContext.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelopeExtensions.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class MessageEnvelopeHandlerNameTests {
  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static MessageEnvelope<JsonElement> _jsonEnvelope(MessageDispatchContext context) => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{\"value\":1}").RootElement,
    Hops = [],
    DispatchContext = context,
  };

  [Test]
  public async Task ReconstructWithPayload_WithHandlerName_StampsItOnTheDispatchContextAsync() {
    var envelope = _jsonEnvelope(new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox });

    var typed = envelope.ReconstructWithPayload(new object(), "OrderHandler");

    await Assert.That(typed.DispatchContext.HandlerName).IsEqualTo("OrderHandler");
  }

  [Test]
  public async Task ReconstructWithPayload_WithHandlerName_PreservesEverythingElseAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox, IsDefaultDispatch = true };
    var envelope = _jsonEnvelope(context);
    var payload = new object();

    var typed = envelope.ReconstructWithPayload(payload, "OrderHandler");

    await Assert.That(typed.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(typed.Payload).IsSameReferenceAs(payload);
    await Assert.That(typed.Hops).IsSameReferenceAs(envelope.Hops);
    await Assert.That(typed.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(typed.DispatchContext.Source).IsEqualTo(MessageSource.Inbox);
    await Assert.That(typed.DispatchContext.IsDefaultDispatch).IsTrue();
  }

  [Test]
  public async Task ReconstructWithPayload_NullHandlerName_LeavesTheContextUntouchedAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    var envelope = _jsonEnvelope(context);

    var typed = envelope.ReconstructWithPayload(new object(), handlerName: null);

    await Assert.That(typed.DispatchContext).IsSameReferenceAs(context)
      .Because("without a handler there is nothing to stamp; the original context flows through as before");
    await Assert.That(typed.DispatchContext.HandlerName).IsNull();
  }

  [Test]
  public async Task ReconstructWithPayload_TwoArgumentOverload_StampsNoHandlerNameAsync() {
    var envelope = _jsonEnvelope(new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local });

    var typed = envelope.ReconstructWithPayload(new object());

    await Assert.That(typed.DispatchContext.HandlerName).IsNull();
  }

  [Test]
  public async Task DispatchContext_HandlerName_IsNeverSerializedAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox, HandlerName = "OrderHandler" };

    var json = JsonSerializer.Serialize(context, _jsonOpts.GetTypeInfo(typeof(MessageDispatchContext)));

    await Assert.That(json).DoesNotContain("OrderHandler")
      .Because("the handler name is an in-process dispatch detail; persisted and transported envelopes must be byte-identical to before");
    await Assert.That(json).DoesNotContain("HandlerName");
  }

  [Test]
  public async Task DispatchContext_RoundTrippedThroughTheWireForm_LosesTheHandlerNameAndKeepsTheRestAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox, IsDefaultDispatch = true, HandlerName = "OrderHandler" };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageDispatchContext));

    var json = JsonSerializer.Serialize(context, typeInfo);
    var restored = JsonSerializer.Deserialize(json, typeInfo) as MessageDispatchContext;

    await Assert.That(restored).IsNotNull();
    await Assert.That(restored!.HandlerName).IsNull()
      .Because("a consumer reading the row or the transport message sees the same context it always did");
    await Assert.That(restored.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(restored.Source).IsEqualTo(MessageSource.Inbox);
    await Assert.That(restored.IsDefaultDispatch).IsTrue();
  }

  [Test]
  public async Task DispatchContext_WithDefaultDispatch_KeepsHandlerNameAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = "OrderHandler" };

    var flipped = context.WithDefaultDispatch();

    await Assert.That(flipped.IsDefaultDispatch).IsTrue();
    await Assert.That(flipped.HandlerName).IsEqualTo("OrderHandler")
      .Because("the cascade wrapper flips the default-dispatch flag on the same handling; the handler identity must survive it");
  }
}
