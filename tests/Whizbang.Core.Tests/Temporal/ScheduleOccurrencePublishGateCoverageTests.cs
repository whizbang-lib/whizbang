using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Coverage for two <see cref="ScheduleOccurrencePublishGate"/> paths
/// <see cref="ScheduleOccurrencePublishGateTests"/> doesn't reach: a <c>Defer</c> verdict with no
/// <see cref="IScheduleOccurrenceStore"/> registered to persist the deferral, and
/// <see cref="ScheduleOccurrencePublishGate.TryReadOccurrence"/> given metadata that fails to
/// parse as JSON entirely (every existing case is well-formed JSON that merely lacks a
/// <c>scheduleId</c>). A defer that silently dropped the occurrence when there is nowhere to
/// reschedule it would lose a job instead of running it now; unparseable metadata that threw
/// instead of failing safe would take down the whole gate over one corrupted row.
/// </summary>
public class ScheduleOccurrencePublishGateCoverageTests {
  private static readonly Guid _schedule = Guid.Parse("33333333-3333-3333-3333-333333333333");

  private sealed class _fixedDecisionHook(FireDecision decision) : IScheduleFireHook {
    public ValueTask<FireDecision> OnBeforeFireAsync(ScheduleFireContext context, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(decision);
  }

  private static OutboxWork _work(string? metadataJson) {
    var id = TrackedGuid.NewMedo().Value;
    return new OutboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement,
      },
      EnvelopeType = "env",
      MessageType = "ProbeOccurrence",
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
      MetadataJson = metadataJson,
    };
  }

  private static string _occurrenceMetadata() =>
    $$"""{"scheduleId":"{{_schedule}}","occurrence":1}""";

  /// <summary>What breaks: without a store to persist the deferral, silently dropping the
  /// occurrence loses the job entirely — running it now instead is strictly safer than losing it,
  /// which is exactly what this fallback trades for.</summary>
  [Test]
  public async Task EvaluateAsync_DeferWithNoStoreRegistered_ProceedsInsteadOfLosingTheOccurrenceAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IScheduleFireHook>(new _fixedDecisionHook(FireDecision.Defer(DateTimeOffset.UtcNow.AddMinutes(5))));
    // Deliberately no IScheduleOccurrenceStore registered.
    var provider = services.BuildServiceProvider();
    var gate = new ScheduleOccurrencePublishGate(
      provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduleOccurrencePublishGate>.Instance);

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata()));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Proceed)
      .Because("with nowhere to persist a deferral, running the occurrence now beats losing it silently");
  }

  /// <summary>What breaks: a corrupted metadata row must fail safe (treated as "not an
  /// occurrence", so the message proceeds untouched) rather than throw and take the whole gate
  /// down over one bad row.</summary>
  [Test]
  public async Task TryReadOccurrence_MalformedJson_ReturnsFalseAsync() {
    var read = ScheduleOccurrencePublishGate.TryReadOccurrence(
      "{not valid json at all", Guid.NewGuid(), "ProbeOccurrence", out var context);

    await Assert.That(read).IsFalse()
      .Because("unparseable metadata is not an occurrence as far as the gate is concerned — it must fail safe, not throw");
    await Assert.That(context.ScheduleId).IsEqualTo(Guid.Empty)
      .Because("a false result must carry the default (unset) context, never a partially-populated one");
  }
}
