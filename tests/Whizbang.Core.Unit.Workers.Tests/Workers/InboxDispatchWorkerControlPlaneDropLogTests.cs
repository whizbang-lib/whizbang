// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1789: a control-plane row that exhausts its attempts is dropped, never stored, so its log line is the only
/// record of why it failed. The line used to carry the id, type and attempt count and discard the row's last recorded
/// error, so a field log showed thousands of drops and no cause. The dead-letter path keeps that text; the drop path
/// now logs it too, with the cause it names.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[Category("Workers")]
public sealed class InboxDispatchWorkerControlPlaneDropLogTests {
  private static readonly string _checkpointType = TypeNameFormatter.Format(typeof(IntegrityCheckpoint));

  private static string _dropLine(InboxDispatchUnitHarness h) =>
    h.Logs.Single(l => l.EventId == 11).Message;

  [Test]
  public async Task Drop_WithARecordedFailure_LogsTheErrorAndNamesItAsARecordedFailureAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11,
      error: "Npgsql.PostgresException: 40P01: deadlock detected", messageType: _checkpointType);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var line = _dropLine(h);
    await Assert.That(h.Logs.Single(l => l.EventId == 11).Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(line).Contains(work.MessageId.ToString())
      .And.Contains("40P01: deadlock detected")
      .Because("the row is dropped, never stored, so this line is the only place its last error survives");
    await Assert.That(line).Contains("a recorded failure")
      .Because("an operator has to tell a message that failed from one that was never tried");
  }

  [Test]
  public async Task Drop_AfterLeaseLapses_NamesTheLapseAsTheCauseAsync() {
    var h = new InboxDispatchUnitHarness();
    var stamp = "Attempt 3 ended without a reported outcome: lease held by instance 0198c0de-0000-7000-8000-000000000001 expired";
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: stamp, messageType: _checkpointType);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var line = _dropLine(h);
    await Assert.That(line).Contains("lease lapsed without a reported outcome")
      .Because("attempts spent on lapsed leases are the framework's doing, not the message's, and the line must say so");
    await Assert.That(line).Contains(stamp);
  }

  [Test]
  public async Task Drop_WithNothingRecorded_SaysNoneRecordedAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: null, messageType: _checkpointType);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_dropLine(h)).Contains("none recorded")
      .Because("an absent error is itself a fact worth stating, distinct from an empty placeholder");
  }

  [Test]
  public async Task Drop_CommitRequest_CarriesTheMessageTypeAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: _checkpointType);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var request = h.Commits.All.Single();
    await Assert.That(request.MessageType).IsEqualTo(_checkpointType)
      .Because("a commit that fails later logs the type it carries, so every commit request names its row's type");
  }
}
