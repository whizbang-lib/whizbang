using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="SagaDeadlineScheduler"/> — a deadline is a keyed one-shot schedule whose id
/// is derived deterministically from (saga stream, deadline name), so re-arming is idempotent and cancel
/// needs no caller bookkeeping. Uses a fake <see cref="IScheduleManager"/> (no database).
/// </summary>
/// <docs>fundamentals/temporal/saga-deadlines</docs>
public class SagaDeadlineSchedulerTests {
  private sealed class FakeManager : IScheduleManager {
    public ScheduleDefinition? LastDefinition { get; private set; }
    public Guid? LastCancelledId { get; private set; }

    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) {
      LastDefinition = definition;
      return Task.FromResult(new ScheduleHandle(definition.ScheduleId ?? Guid.Empty, definition.StartAt ?? default, true));
    }
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) {
      LastCancelledId = scheduleId;
      return Task.FromResult(true);
    }
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
      Task.FromResult<Guid?>(Guid.NewGuid());
    public Task<ScheduleUpdateResult?> UpdateAsync(
      Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult<ScheduleUpdateResult?>(null);
  }

  private static readonly Guid _saga = Guid.Parse("11111111-2222-3333-4444-555555555555");
  private static readonly DateTimeOffset _at = new(2026, 07, 20, 09, 00, 00, TimeSpan.Zero);

  [Test]
  public async Task SetDeadline_CreatesKeyedOneShotOnSagaStreamAsync() {
    var fake = new FakeManager();
    var sut = new SagaDeadlineScheduler(fake);

    _ = await sut.SetDeadlineAsync(_saga, "payment-timeout", _at, "PaymentTimedOut");

    var def = fake.LastDefinition!;
    await Assert.That(def.Kind).IsEqualTo(RecurrenceKind.OneShot);
    await Assert.That(def.StartAt).IsEqualTo(_at);
    await Assert.That(def.StreamId).IsEqualTo(_saga);          // fires on the saga's own stream
    await Assert.That(def.EventType).IsEqualTo("PaymentTimedOut");
    await Assert.That(def.Key).IsEqualTo($"saga:{_saga:N}:payment-timeout");
    await Assert.That(def.ScheduleId).IsEqualTo(sut.DeadlineScheduleId(_saga, "payment-timeout"));
  }

  [Test]
  public async Task DeadlineScheduleId_IsDeterministicAsync() {
    var sut = new SagaDeadlineScheduler(new FakeManager());

    var a = sut.DeadlineScheduleId(_saga, "payment-timeout");
    var b = sut.DeadlineScheduleId(_saga, "payment-timeout");

    await Assert.That(a).IsEqualTo(b);          // re-arming targets the same row => idempotent
    await Assert.That(a).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task DeadlineScheduleId_DiffersByNameAndBySagaAsync() {
    var sut = new SagaDeadlineScheduler(new FakeManager());
    var other = Guid.Parse("99999999-8888-7777-6666-555555555555");

    await Assert.That(sut.DeadlineScheduleId(_saga, "payment-timeout"))
      .IsNotEqualTo(sut.DeadlineScheduleId(_saga, "shipping-timeout"));
    await Assert.That(sut.DeadlineScheduleId(_saga, "payment-timeout"))
      .IsNotEqualTo(sut.DeadlineScheduleId(other, "payment-timeout"));
  }

  [Test]
  public async Task CancelDeadline_CancelsTheDerivedScheduleAsync() {
    var fake = new FakeManager();
    var sut = new SagaDeadlineScheduler(fake);

    var cancelled = await sut.CancelDeadlineAsync(_saga, "payment-timeout");

    await Assert.That(cancelled).IsTrue();
    await Assert.That(fake.LastCancelledId).IsEqualTo(sut.DeadlineScheduleId(_saga, "payment-timeout"));
  }

  [Test]
  public async Task SetDeadline_RejectsEmptyNameOrEventTypeAsync() {
    var sut = new SagaDeadlineScheduler(new FakeManager());

    await Assert.That(async () => await sut.SetDeadlineAsync(_saga, "  ", _at, "E")).Throws<ArgumentException>();
    await Assert.That(async () => await sut.SetDeadlineAsync(_saga, "d", _at, "  ")).Throws<ArgumentException>();
  }
}
