// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Sagas.Component.Tests;

/// <summary>
/// The parts of the first-registration-wins per-item stream namespace (#2021) that need threads or a host start: the
/// registration settles on one value however many threads race it, and the conflict warning is logged when its host
/// starts. The unit tests of the choice itself are in <c>Whizbang.Sagas.Tests</c>
/// (<c>SagaItemStreamsFirstRegistrationWinsTests</c>). Documented at
/// <c>docs/fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/FirstRegistrationSlot.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/SagaItemStreamNamespaceConflictWarning.cs</code-under-test>
[Category("Component")]
[Category("Saga")]
public class FirstRegistrationSlotConcurrencyTests {

  /// <summary>
  /// Registrations racing from several threads (hosts built in parallel) settle on exactly one value, and every
  /// registration is told which one: the value cannot be written by one registration and read back as another's. A slot
  /// of its own, so no other test's registration can take part in the race.
  /// </summary>
  [Test]
  public async Task ConcurrentRegistrations_SettleOnOneValue_ThatEveryRegistrationIsToldAsync() {
    var slot = new FirstRegistrationSlot();
    var requested = Enumerable.Range(0, 64).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    var kept = new Guid[requested.Length];

    Parallel.For(0, requested.Length, i => kept[i] = slot.Register(requested[i]));

    var winner = slot.Current;
    await Assert.That(winner.HasValue).IsTrue();
    await Assert.That(requested).Contains(winner!.Value).Because("the value kept is one a registration asked for");
    await Assert.That(kept.All(k => k == winner.Value)).IsTrue()
      .Because("every registration, the winner included, is told the one value the process uses");
  }

  [Test]
  public async Task TheConflictWarning_IsLoggedWhenItsHostStarts_AndStopsWithNothingToDoAsync() {
    var kept = Guid.Parse("1a2b3c4d-0000-4000-8000-000000002021");
    var requested = Guid.Parse("5e6f7a8b-0000-4000-8000-000000002021");
    var logger = new RecordingLogger();
    var warning = new SagaItemStreamNamespaceConflictWarning(logger, kept, requested);

    await warning.StartAsync(CancellationToken.None);
    await warning.StopAsync(CancellationToken.None);

    var warnings = logger.Warnings;
    await Assert.That(warnings.Count).IsEqualTo(1);
    await Assert.That(warnings[0]).Contains(kept.ToString());
    await Assert.That(warnings[0]).Contains(requested.ToString());
  }

  private sealed class RecordingLogger : ILogger<SagaItemStreamNamespaceConflictWarning> {
    private readonly List<string> _warnings = [];
    public IReadOnlyList<string> Warnings { get { lock (_warnings) { return [.. _warnings]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (_warnings) {
          _warnings.Add(formatter(state, exception));
        }
      }
    }
  }
}
