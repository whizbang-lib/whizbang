using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Builds message envelopes for transport helper tests.
/// Mirrors the envelope shape produced by <c>TransportTestHarness.Create</c>.
/// </summary>
internal static class EnvelopeFactory {
  public static MessageEnvelope<TestPayload> Create(string content) {
    return CreateFor(new TestPayload { Content = content });
  }

  public static MessageEnvelope<TPayload> CreateFor<TPayload>(TPayload payload) where TPayload : class {
    return new MessageEnvelope<TPayload> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          TraceParent = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }
}
