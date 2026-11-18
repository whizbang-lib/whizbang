// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2253: the framework's steady-state integrity traffic is not work for the housekeeping idle gate. The checkpoint every
/// peer publishes, and the redelivery requests and answers it sets off, arrive as background-band rows whether or not the
/// service has any application work, and each one used to reset the gate's dwell, so maintenance only ever ran as the
/// hourly forced pass. The receptors that tell a row in flight from a row missing still count them.
/// </summary>
/// <docs>operations/workers/housekeeping-arbitration#what-counts-as-work</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/SteadyIntegrityTraffic.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
[Category("Messaging")]
public class SteadyIntegrityTrafficTests {

  [Test]
  public async Task TheTraffic_IsTheCheckpoint_TheRedeliveryRequest_AndItsAnswerAsync() {
    // Compared as names: an equivalence assertion over Type objects walks their members by reflection.
    await Assert.That(SteadyIntegrityTraffic.Types.Select(t => t.Name))
      .IsEquivalentTo([nameof(IntegrityCheckpoint), nameof(RequestRedeliveryCommand), nameof(RedeliveryComposite)]);
    await Assert.That(SteadyIntegrityTraffic.Types.Contains(typeof(RedeliveryComposite))).IsTrue();
    await Assert.That(SteadyIntegrityTraffic.StoredNames)
      .IsEquivalentTo(SteadyIntegrityTraffic.Types.Select(TypeNameFormatter.Format))
      .Because("the reading compares them with the normalized message_type, which is the stored form");
  }

  [Test]
  public async Task OnlyIntegrityTraffic_IsQuiescentApartFromIntegrity_ButNotQuiescentAsync() {
    var integrityOnly = new ServiceBacklog { UnprocessedInboxRows = 2, ActiveLeasedRows = 1, PendingOutboxRows = 1, NonIntegrityWorkRows = 0 };

    await Assert.That(integrityOnly.IsQuiescentApartFromIntegrity).IsTrue()
      .Because("a service whose only rows are the framework's integrity chatter has no work the sweep could get in the way of");
    await Assert.That(integrityOnly.IsQuiescent).IsFalse()
      .Because("the integrity receptors still read the rows as in flight, so a redelivery in progress is never called missing");
  }

  [Test]
  public async Task ApplicationWork_PerspectiveRows_OrTheIdleBand_AreNotQuiescentAsync() {
    var cases = new[] {
      new ServiceBacklog { UnprocessedInboxRows = 3, NonIntegrityWorkRows = 1 },
      new ServiceBacklog { NonIntegrityWorkRows = 0, PendingPerspectiveRows = 1 },
      new ServiceBacklog { NonIntegrityWorkRows = 0, PendingIdleRows = 1 },
    };

    foreach (var backlog in cases) {
      await Assert.That(backlog.IsQuiescentApartFromIntegrity).IsFalse().Because(backlog.ToString());
    }
  }

  [Test]
  public async Task AnUnmeasuredFigure_FallsBackToQuiescentAsync() {
    await Assert.That(new ServiceBacklog { UnprocessedInboxRows = 1 }.IsQuiescentApartFromIntegrity).IsFalse()
      .Because("a backend that cannot tell integrity traffic apart counts every row, as before");
    await Assert.That(new ServiceBacklog().IsQuiescentApartFromIntegrity).IsTrue();
  }

  // ===== #2263: what admits the idle drain =====

  [Test]
  public async Task OnlyIntegrityTraffic_IsSettledApartFromIntegrity_ButNotSettledAsync() {
    var integrityOnly = new ServiceBacklog { UnprocessedInboxRows = 2, ActiveLeasedRows = 1, PendingOutboxRows = 1, NonIntegrityWorkRows = 0 };

    await Assert.That(integrityOnly.IsSettledApartFromIntegrity).IsTrue()
      .Because("the idle drain is not held back by the framework's integrity chatter");
    await Assert.That(integrityOnly.IsSettled).IsFalse()
      .Because("the integrity receptors still read the rows as in flight");
  }

  [Test]
  public async Task ApplicationWork_OrPerspectiveRows_AreNotSettled_ButTheIdleBandIsAsync() {
    await Assert.That(new ServiceBacklog { UnprocessedInboxRows = 3, NonIntegrityWorkRows = 1 }.IsSettledApartFromIntegrity).IsFalse();
    await Assert.That(new ServiceBacklog { NonIntegrityWorkRows = 0, PendingPerspectiveRows = 1 }.IsSettledApartFromIntegrity).IsFalse();
    await Assert.That(new ServiceBacklog { NonIntegrityWorkRows = 0, PendingIdleRows = 1 }.IsSettledApartFromIntegrity).IsTrue()
      .Because("the idle band is what the drain this reading admits exists to release");
  }

  [Test]
  public async Task AnUnmeasuredFigure_FallsBackToSettledAsync() {
    await Assert.That(new ServiceBacklog { UnprocessedInboxRows = 1 }.IsSettledApartFromIntegrity).IsFalse()
      .Because("a backend that cannot tell integrity traffic apart counts every row, as before");
    await Assert.That(new ServiceBacklog { PendingIdleRows = 1 }.IsSettledApartFromIntegrity).IsTrue();
  }
}
