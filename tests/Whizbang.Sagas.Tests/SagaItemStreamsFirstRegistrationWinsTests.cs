// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The process-wide per-item stream namespace is first-registration-wins (#2021): the first <c>AddWhizbangSagas</c> in a
/// process sets <see cref="SagaItemStreams.AppDefaultNamespace"/>, a later registration that disagrees leaves it alone and
/// warns naming both namespaces, and a later registration that agrees is silent. A host's own saga services are not
/// affected: they derive under their own options through <see cref="ISagaItemStreamIdDeriver"/>.
/// </summary>
/// <remarks>
/// The race-safety of the registration, which needs threads, is in the Component project
/// (<c>FirstRegistrationSlotConcurrencyTests</c>). Documented at <c>docs/fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</c>. These tests fail
/// if the choice flips back to last-registration-wins, or if a disagreement stops being reported.
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/SagaItemStreams.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/SagaServiceCollectionExtensions.cs</code-under-test>
[Category("Unit")]
[Category("Saga")]
[NotInParallel(ProcessWideSagaNamespace.NOT_IN_PARALLEL_KEY)]
public class SagaItemStreamsFirstRegistrationWinsTests {
  private static readonly Guid _first = Guid.Parse("1a2b3c4d-0000-4000-8000-000000002021");
  private static readonly Guid _second = Guid.Parse("5e6f7a8b-0000-4000-8000-000000002021");

  [Test]
  public async Task ALaterRegistrationWithADifferentNamespace_DoesNotOverwriteTheFirstAsync() {
    using var _ = SagaItemStreams.ResetAppDefaultNamespaceForTesting();

    new ServiceCollection().AddWhizbangSagas(o => o.PerItemStreamNamespace = _first);
    new ServiceCollection().AddWhizbangSagas(o => o.PerItemStreamNamespace = _second);

    await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(_first)
      .Because("the first registration's namespace is kept; a later host in the same process must not re-route every "
             + "caller of the namespace-less SagaItemStreams.Of to its own streams");
  }

  [Test]
  public async Task ALaterRegistrationWithADifferentNamespace_WarnsNamingBothAsync() {
    using var _ = SagaItemStreams.ResetAppDefaultNamespaceForTesting();
    new ServiceCollection().AddWhizbangSagas(o => o.PerItemStreamNamespace = _first);

    var logger = new RecordingLogger();
    await _reportAsync(o => o.PerItemStreamNamespace = _second, logger);

    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("a registration whose namespace is not applied process-wide says so, once");
    await Assert.That(warnings[0]).Contains(_first.ToString());
    await Assert.That(warnings[0]).Contains(_second.ToString())
      .Because("the warning names the namespace that was kept and the one that was not, so the conflict can be found");
  }

  [Test]
  public async Task ALaterRegistrationWithTheSameNamespace_IsSilentAsync() {
    using var _ = SagaItemStreams.ResetAppDefaultNamespaceForTesting();
    new ServiceCollection().AddWhizbangSagas(o => o.PerItemStreamNamespace = _first);

    var logger = new RecordingLogger();
    await _reportAsync(o => o.PerItemStreamNamespace = _first, logger);

    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse()
      .Because("a registration that agrees with the process-wide namespace changes nothing and has nothing to report");
    await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(_first);
  }

  [Test]
  public async Task TheFirstRegistration_IsAppliedAndIsSilentAsync() {
    using var _ = SagaItemStreams.ResetAppDefaultNamespaceForTesting();

    var logger = new RecordingLogger();
    await _reportAsync(o => o.PerItemStreamNamespace = _second, logger);

    await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(_second);
    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse();
  }

  [Test]
  public async Task ResetForTesting_DisposingTheScope_PutsBackTheRegistrationItClearedAsync() {
    using (SagaItemStreams.ResetAppDefaultNamespaceForTesting()) {
      SagaItemStreams.RegisterAppDefaultNamespace(_first);
      using (SagaItemStreams.ResetAppDefaultNamespaceForTesting()) {
        await Assert.That(SagaItemStreams.RegisterAppDefaultNamespace(_second)).IsEqualTo(_second)
          .Because("inside the scope nothing is registered, so the next registration is the first");
      }
      await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(_first);
      await Assert.That(SagaItemStreams.RegisterAppDefaultNamespace(_second)).IsEqualTo(_first)
        .Because("the restored registration still wins against a later one");
    }
  }

  /// <summary>
  /// Registers sagas in a container of its own and has every namespace-conflict warning it registered report, as each
  /// does when its host starts (the start itself is covered in the Component project). A registration that agrees
  /// registers no warning at all.
  /// </summary>
  private static async Task _reportAsync(Action<SagaOptions> configure, RecordingLogger logger) {
    var services = new ServiceCollection();
    services.AddSingleton<ILoggerFactory>(new RecordingLoggerFactory(logger));
    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    services.AddWhizbangSagas(configure);
    await using var provider = services.BuildServiceProvider();
    foreach (var warning in provider.GetServices<IHostedService>().OfType<SagaItemStreamNamespaceConflictWarning>()) {
      warning.Report();
    }
  }

  private sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory {
    public ILogger CreateLogger(string categoryName) => logger;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
  }

  private sealed class RecordingLogger : ILogger {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    public IReadOnlyList<(LogLevel Level, string Message)> Entries { get { lock (_entries) { return [.. _entries]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_entries) {
        _entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }
}
