using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The advisory's finding is emitted as a system event, not only logged.
/// </summary>
/// <remarks>
/// A log line is the end of the road: the host cannot route it, and the framework should not decide
/// what an advisory becomes. These cases cover the emission itself, what happens when there is
/// nothing to emit through, and what happens when emitting fails -- which must not cost the rest of
/// the statistics cycle, since the queue depths and bloat ratio measured after it have nothing to
/// do with an advisory.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
[Category("Observability")]
public class PerspectiveIndexAdvisoryEmissionTests {

  private sealed class CapturingEmitter : ISystemEventEmitter {
    public List<ISystemEvent> Emitted { get; } = [];
    public Exception? Throw { get; init; }

    /// <summary>Calls made, including ones that threw, so a failure can be shown to have been tried.</summary>
    public int Attempts { get; private set; }

    public Task EmitAsync<TSystemEvent>(TSystemEvent systemEvent, CancellationToken cancellationToken = default)
        where TSystemEvent : ISystemEvent {
      Attempts++;
      if (Throw is not null) {
        throw Throw;
      }
      Emitted.Add(systemEvent);
      return Task.CompletedTask;
    }

    public Task EmitEventAuditedAsync<TEvent>(
        Guid streamId, long streamPosition, MessageEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(
        TCommand command, TResponse response, string receptorName,
        IMessageContext? context,
        CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;

    public bool ShouldExcludeFromAudit(Type type) => false;
  }

  private static PerspectiveIndexAdvised _finding(string model = "BigModel") => new() {
    ModelName = model,
    TableName = "wh_per_big",
    TableSizeBytes = 512L * 1024 * 1024,
    ThresholdBytes = 256L * 1024 * 1024,
    Exposure = "Sortable",
    UnindexedFields = ["TenantId"],
  };

  [Test]
  public async Task EachFinding_IsEmittedAsync() {
    var emitter = new CapturingEmitter();

    await TableStatisticsCollector.EmitFindingsAsync(
      [_finding("One"), _finding("Two")], emitter, NullLogger.Instance, CancellationToken.None);

    await Assert.That(emitter.Emitted).Count().IsEqualTo(2)
      .Because("every finding the advisory made has to reach the host, not just the first.");
    await Assert.That(emitter.Emitted.OfType<PerspectiveIndexAdvised>().Select(e => e.ModelName))
      .Contains("One");
  }

  /// <summary>Without an emitter the finding is still reported, just not routable.</summary>
  [Test]
  public async Task WithoutAnEmitter_NothingIsEmittedAndNothingThrowsAsync() {
    await Assert.That(async () => await TableStatisticsCollector.EmitFindingsAsync(
        [_finding()], emitter: null, NullLogger.Instance, CancellationToken.None))
      .ThrowsNothing()
      .Because("the emitter is optional: a host that registered none still gets the log line, and "
             + "the statistics cycle must not fault looking for one.");
  }

  [Test]
  public async Task WithNoFindings_NothingIsEmittedAsync() {
    var emitter = new CapturingEmitter();

    await TableStatisticsCollector.EmitFindingsAsync(
      [], emitter, NullLogger.Instance, CancellationToken.None);

    await Assert.That(emitter.Emitted).IsEmpty();
  }

  /// <summary>
  /// A failed emission is swallowed, because the rest of the cycle does not depend on it.
  /// </summary>
  [Test]
  public async Task AFailedEmission_DoesNotEndTheCycleAsync() {
    var emitter = new CapturingEmitter { Throw = new InvalidOperationException("transport down") };

    await TableStatisticsCollector.EmitFindingsAsync(
      [_finding()], emitter, NullLogger.Instance, CancellationToken.None);

    await Assert.That(emitter.Attempts).IsEqualTo(1)
      .Because("it has to have tried: swallowing the finding without attempting it would look the "
             + "same from outside and would be a different bug.");
    await Assert.That(emitter.Emitted).IsEmpty()
      .Because("and the attempt failed, so nothing was emitted -- yet the call returned, because an "
             + "advisory is the least important thing the statistics cycle does and losing it must "
             + "not cost the queue depths and bloat ratio measured after it.");
  }

  /// <summary>
  /// Cancellation is not swallowed, because it is not a failure to report.
  /// </summary>
  /// <remarks>
  /// Distinguished from the case above on purpose: treating a shutdown as a failed emission would
  /// log a warning on every stop, and would keep the loop going through a cancellation the host
  /// asked for.
  /// </remarks>
  [Test]
  public async Task Cancellation_IsNotTreatedAsAFailedEmissionAsync() {
    var emitter = new CapturingEmitter { Throw = new OperationCanceledException() };

    await Assert.That(async () => await TableStatisticsCollector.EmitFindingsAsync(
        [_finding()], emitter, NullLogger.Instance, CancellationToken.None))
      .Throws<OperationCanceledException>();
  }

  /// <summary>The advisory event is enabled by the flag that covers perspective events.</summary>
  /// <remarks>
  /// One switch rather than another for a single event: a host that asked for perspective events
  /// wants to hear this one, and a host that did not is not asking to be advised.
  /// </remarks>
  [Test]
  public async Task TheAdvisoryEvent_FollowsThePerspectiveEventsFlagAsync() {
    await Assert.That(new SystemEventOptions().IsEnabled<PerspectiveIndexAdvised>()).IsFalse()
      .Because("nothing is enabled by default.");
    await Assert.That(new SystemEventOptions().EnablePerspectiveEvents()
        .IsEnabled<PerspectiveIndexAdvised>()).IsTrue();
    await Assert.That(new SystemEventOptions().EnableEventAudit()
        .IsEnabled<PerspectiveIndexAdvised>()).IsFalse()
      .Because("enabling audit is not asking to be advised about indexes.");
  }
}
