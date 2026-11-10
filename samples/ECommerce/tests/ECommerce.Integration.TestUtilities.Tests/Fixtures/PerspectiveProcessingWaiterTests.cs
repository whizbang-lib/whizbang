// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using ECommerce.Integration.TestUtilities.Fixtures;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;

namespace ECommerce.Integration.TestUtilities.Tests.Fixtures;

/// <summary>A stand-in perspective whose CLR name the fake source reports, as the worker reports a real one's.</summary>
internal sealed class CatalogPerspective;

/// <summary>A second stand-in perspective that applies two events per stream.</summary>
internal sealed class InventoryPerspective;

/// <summary>
/// <para>The applied-events wait counts distinct events per (perspective, stream) and completes only when every stream
/// meets every perspective's expectation (#2237). The summed wait it replaces could be met by reports that are not new
/// applies (a rewind re-counting its trigger, a range read naming events of other types), so a seed test read eleven
/// products after its wait for twelve had returned. Here a fake source over-reports one stream and under-reports
/// another, the shape that met the old sum, and the wait holds until the short stream is applied.</para>
/// </summary>
/// <code-under-test>samples/ECommerce/tests/ECommerce.Integration.TestUtilities/Fixtures/PerspectiveProcessingWaiter.cs</code-under-test>
public sealed class PerspectiveProcessingWaiterTests {
  private static readonly PerspectiveApplyExpectation[] _expectations = [
    new(typeof(CatalogPerspective), EventsPerStream: 1),
    new(typeof(InventoryPerspective), EventsPerStream: 2),
  ];

  [Test]
  public async Task OverReportedAndUnderReportedStreams_HoldTheWait_UntilEveryStreamMeetsEveryPerspectiveAsync() {
    var source = new FakeSource();
    Guid streamA = Guid.CreateVersion7(), streamB = Guid.CreateVersion7();
    Guid a1 = Guid.CreateVersion7(), a2 = Guid.CreateVersion7(), b1 = Guid.CreateVersion7(), b2 = Guid.CreateVersion7();
    var wait = PerspectiveProcessingWaiter.WaitForAppliedAsync([source], [streamA, streamB], _expectations, CancellationToken.None);

    // Stream A is over-reported: repeated reports and a batch naming more events than were applied.
    source.Report<CatalogPerspective>(streamA, a1);
    source.Report<CatalogPerspective>(streamA, a1);
    source.Report<InventoryPerspective>(streamA, a1, a2, Guid.CreateVersion7());
    source.Report<InventoryPerspective>(streamA, a2);
    // Stream B is under-reported: its inventory perspective has applied one of its two events.
    source.Report<CatalogPerspective>(streamB, b1);
    source.Report<InventoryPerspective>(streamB, b1);

    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("eight reported events would meet a summed wait for six, but stream B's inventory has applied one of two");

    source.Report<InventoryPerspective>(streamB, b2);
    await wait;
    await Assert.That(source.SubscriberCount).IsEqualTo(0)
      .Because("the wait unsubscribes once it completes");
  }

  [Test]
  public async Task ReportsForOtherStreamsAndPerspectives_DoNotCountAsync() {
    var source = new FakeSource();
    var stream = Guid.CreateVersion7();
    var wait = PerspectiveProcessingWaiter.WaitForAppliedAsync(
      [null, source], [stream], [new(typeof(CatalogPerspective), EventsPerStream: 1)], CancellationToken.None);

    source.Report<CatalogPerspective>(Guid.CreateVersion7(), Guid.CreateVersion7());
    source.Report<InventoryPerspective>(stream, Guid.CreateVersion7());

    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("another stream's report and another perspective's report say nothing about this pair");

    source.Report<CatalogPerspective>(stream, Guid.CreateVersion7());
    await wait;
  }

  [Test]
  public async Task Canceled_BeforeEveryPairIsMet_ThrowsTimeout_NamingWhatIsMissingAsync() {
    var source = new FakeSource();
    Guid streamA = Guid.CreateVersion7(), streamB = Guid.CreateVersion7();
    using var cts = new CancellationTokenSource();
    var wait = PerspectiveProcessingWaiter.WaitForAppliedAsync([source], [streamA, streamB], _expectations, cts.Token);
    source.Report<CatalogPerspective>(streamA, Guid.CreateVersion7());
    source.Report<InventoryPerspective>(streamA, Guid.CreateVersion7(), Guid.CreateVersion7());
    source.Report<InventoryPerspective>(streamB, Guid.CreateVersion7());

    await cts.CancelAsync();
    var thrown = await Assert.That(async () => await wait).Throws<TimeoutException>();

    await Assert.That(thrown!.Message).Contains(streamB.ToString());
    await Assert.That(thrown.Message).Contains($"{_name<CatalogPerspective>()} 0 of 1");
    await Assert.That(thrown.Message).Contains($"{_name<InventoryPerspective>()} 1 of 2");
    await Assert.That(thrown.Message).DoesNotContain(streamA.ToString())
      .Because("a pair that met its expectation is not reported as missing");
    await Assert.That(source.SubscriberCount).IsEqualTo(0)
      .Because("the wait unsubscribes when it gives up, too");
  }

  [Test]
  public async Task NoStreams_IsRefused_BecauseAnUnfilteredWaitCountsOtherTestsEventsAsync() {
    await Assert.That(() => PerspectiveProcessingWaiter.WaitForAppliedAsync([new FakeSource()], [], _expectations, CancellationToken.None))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task NoExpectations_IsRefused_BecauseTheWaitWouldMeanNothingAsync() {
    await Assert.That(() => PerspectiveProcessingWaiter.WaitForAppliedAsync([new FakeSource()], [Guid.CreateVersion7()], [], CancellationToken.None))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task APerspectiveTypeWithNoClrName_IsRefusedAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(() => PerspectiveProcessingWaiter.WaitForAppliedAsync(
        [new FakeSource()], [Guid.CreateVersion7()], [new(genericParameter, EventsPerStream: 1)], CancellationToken.None))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ASourceForNoWorker_IsNoSourceAsync() {
    await Assert.That(PerspectiveWorkerEventSource.Of(null)).IsNull();
  }

  private static string _name<TPerspective>() =>
    TypeNameFormatter.TryFormatClrTypeName(typeof(TPerspective), out var name) ? name : throw new InvalidOperationException();

  private sealed class FakeSource : IPerspectiveEventProcessedSource {
    private PerspectiveEventProcessedHandler? _handlers;

    public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

    public event PerspectiveEventProcessedHandler? OnPerspectiveEventProcessed {
      add => _handlers += value;
      remove => _handlers -= value;
    }

    public void Report<TPerspective>(Guid streamId, params Guid[] eventIds) =>
      _handlers?.Invoke(new PerspectiveEventProcessedEvent {
        PerspectiveName = _name<TPerspective>(),
        StreamId = streamId,
        EventCount = eventIds.Length,
        EventIds = eventIds,
      });
  }
}
