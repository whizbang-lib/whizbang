// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1901: the dead-letter record is one event a consumer can alert on. Its EventId and name are a contract: an
/// alert or a query keyed on them stops finding dead letters if either changes.
/// </summary>
/// <docs>operations/observability/logging-categories#dead-lettered-messages</docs>
[Category("Observability")]
public sealed class DeadLetterLogTests {
  [Test]
  public async Task MessageDeadLettered_LogsTheDocumentedEventIdAndNameAsync() {
    var logger = new FakeLogger();

    DeadLetterLog.MessageDeadLettered(logger, LogLevel.Error, exception: null, Guid.Empty, "T", "R", "E");

    var record = logger.Collector.LatestRecord;
    await Assert.That(record.Id.Id).IsEqualTo(1901);
    await Assert.That(record.Id.Name).IsEqualTo("MessageDeadLettered");
  }

  [Test]
  public async Task MessageDeadLettered_NoTypeOrError_SaysSoAsync() {
    var logger = new FakeLogger();

    DeadLetterLog.MessageDeadLettered(logger, LogLevel.Error, exception: null, Guid.Empty, messageType: null, "R", lastError: null);

    await Assert.That(logger.Collector.LatestRecord.Message).Contains("of type unknown")
      .And.Contains("Last error: none recorded")
      .Because("a record with a missing field says it is missing rather than printing a null");
  }

  [Test]
  public async Task BrokerMessageDeadLettered_NullImport_ThrowsAsync() {
    await Assert.That(() => DeadLetterLog.BrokerMessageDeadLettered(NullLogger.Instance, LogLevel.Error, null!))
      .Throws<ArgumentNullException>();
  }
}
