// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
  public async Task EventIdAndName_AreTheDocumentedContractAsync() {
    await Assert.That(DeadLetterLog.EVENT_ID).IsEqualTo(1901);
    await Assert.That(DeadLetterLog.EVENT_NAME).IsEqualTo("MessageDeadLettered");
  }

  [Test]
  public async Task BrokerMessageDeadLettered_NullImport_ThrowsAsync() {
    await Assert.That(() => DeadLetterLog.BrokerMessageDeadLettered(NullLogger.Instance, LogLevel.Error, null!))
      .Throws<ArgumentNullException>();
  }
}
