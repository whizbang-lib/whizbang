// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="ReceivedOriginStampMonitor"/> constructed without a clock. It falls back to the system
/// clock and measures its window from construction, so receives inside the first window are counted
/// without a warning.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedOriginStampMonitor.cs</code-under-test>
[Category("Workers")]
public class ReceivedOriginStampMonitorBranchCoverageTests {

  [Test]
  public async Task NoClockGiven_UsesTheSystemClock_AndStaysQuietInsideTheFirstWindowAsync() {
    var logger = new FakeLogger<ReceivedOriginStampMonitor>();
    var monitor = new ReceivedOriginStampMonitor(Options.Create(new StreamIntegrityOptions()), logger);

    var id = (Guid)TrackedGuid.New();
    monitor.Record(new InboxMessage {
      MessageId = id,
      HandlerName = "OrderShippedHandler",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonDocument.Parse("{}").RootElement, []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.OrderShipped, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Events.OrderShipped, MyApp",
      StreamId = id,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      SourceServiceId = Guid.Empty,
    });

    await Assert.That(logger.Collector.Count).IsEqualTo(0)
      .Because("the window is measured on the system clock from construction, and five minutes have not passed");
  }
}
