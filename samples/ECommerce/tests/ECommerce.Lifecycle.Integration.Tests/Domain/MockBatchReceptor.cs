using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.Lifecycle.Integration.Tests.Domain;

/// <summary>
/// Receptor that handles MockBatchTestCommand by returning MockBatchTestEvent
/// and publishing many MockBatchNoiseEvents to overflow batch processing.
/// </summary>
public class MockBatchTestReceptor(IDispatcher dispatcher, ILogger<MockBatchTestReceptor> logger)
  : IReceptor<MockBatchTestCommand, MockBatchTestEvent> {

  public async ValueTask<MockBatchTestEvent> HandleAsync(
    MockBatchTestCommand command,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "MockBatchTestReceptor: Creating test event and {NoiseCount} noise events for stream {StreamId}",
      command.NoiseEventCount,
      command.StreamId);

    // Publish noise events to flood the stream
    for (int i = 0; i < command.NoiseEventCount; i++) {
      await dispatcher.PublishAsync(new MockBatchNoiseEvent {
        StreamId = command.StreamId,
        Index = i
      });
    }

    // Return the main test event (auto-cascaded by dispatcher)
    return new MockBatchTestEvent {
      StreamId = command.StreamId
    };
  }
}
