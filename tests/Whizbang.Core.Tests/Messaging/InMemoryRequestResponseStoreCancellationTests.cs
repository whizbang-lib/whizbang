using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A waiter's cancellation is the waiter's alone. The store used to cancel the shared completion source
/// when one waiter's token fired, which canceled every other waiter on the same correlation and left the
/// response that arrived afterwards with nowhere to go. The token now governs the wait, not the record.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/InMemoryRequestResponseStore.cs</code-under-test>
public class InMemoryRequestResponseStoreCancellationTests {
  private static MessageEnvelope<string> _response(string payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task WaitForResponseAsync_OneWaiterCanceled_TheOtherStillReceivesTheResponseAsync() {
    var store = new InMemoryRequestResponseStore();
    var correlationId = CorrelationId.New();
    await store.SaveRequestAsync(correlationId, MessageId.New(), TimeSpan.FromSeconds(30), CancellationToken.None);
    using var cts = new CancellationTokenSource();
    var canceledWaiter = store.WaitForResponseAsync(correlationId, cts.Token);
    var patientWaiter = store.WaitForResponseAsync(correlationId, CancellationToken.None);

    await cts.CancelAsync();
    var canceledResult = await canceledWaiter;
    await store.SaveResponseAsync(correlationId, _response("answer"), CancellationToken.None);
    var patientResult = await patientWaiter.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(canceledResult).IsNull()
      .Because("the canceled waiter gives up, as before");
    await Assert.That(patientResult).IsNotNull()
      .Because("cancellation belongs to the waiter, not to the record: the other waiter still gets the response");
    await Assert.That(((MessageEnvelope<string>)patientResult!).Payload).IsEqualTo("answer");
  }

  [Test]
  public async Task WaitForResponseAsyncGeneric_OneWaiterCanceled_TheOtherStillReceivesTheResponseAsync() {
    var store = new InMemoryRequestResponseStore();
    var correlationId = CorrelationId.New();
    await store.SaveRequestAsync(correlationId, MessageId.New(), TimeSpan.FromSeconds(30), CancellationToken.None);
    using var cts = new CancellationTokenSource();
    var canceledWaiter = store.WaitForResponseAsync<string>(correlationId, cts.Token);
    var patientWaiter = store.WaitForResponseAsync<string>(correlationId, CancellationToken.None);

    await cts.CancelAsync();
    var canceledResult = await canceledWaiter;
    await store.SaveResponseAsync(correlationId, _response("typed answer"), CancellationToken.None);
    var patientResult = await patientWaiter.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(canceledResult).IsNull();
    await Assert.That(patientResult).IsNotNull()
      .Because("the typed overload waits the same way; one waiter's token must not cancel another's wait");
    await Assert.That(patientResult!.Payload).IsEqualTo("typed answer");
  }
}
