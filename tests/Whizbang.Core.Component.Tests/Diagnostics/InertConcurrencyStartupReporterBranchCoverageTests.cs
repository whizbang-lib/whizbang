// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Branch coverage for the reporter's option resolution: with no service provider it falls back to
/// <c>IOptions</c>, and with neither a registered singleton nor an <c>IOptions</c> for a setting it
/// treats that setting as absent, so no finding is reported about options nobody supplied.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Diagnostics/InertConcurrencyStartupReporter.cs</code-under-test>
[Category("Diagnostics")]
public class InertConcurrencyStartupReporterBranchCoverageTests {

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
  }

  [Test]
  public async Task StartAsync_NoServiceProvider_ReadsTheIOptionsFallbackAsync() {
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    var reporter = new InertConcurrencyStartupReporter(
      logger,
      services: null!,
      coordinator: Options.Create(new WorkCoordinatorOptions { ParallelizeStreams = false }),
      orderedStream: Options.Create(new OrderedStreamProcessorOptions { ParallelizeStreams = true }),
      outboxDrain: Options.Create(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 }),
      inboxDispatch: Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 }));

    await reporter.StartAsync(CancellationToken.None);

    var warnings = logger.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("with no provider to prefer, the IOptions values are the configuration: outbox parallelism is off "
        + "against a width of 128, and the inbox flag is on");
    await Assert.That(warnings[0].Message).Contains("128");
  }

  [Test]
  public async Task StartAsync_NoSingletonAndNoIOptions_TreatsTheSettingAsAbsentAsync() {
    var logger = new CapturingLogger<InertConcurrencyStartupReporter>();
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var reporter = new InertConcurrencyStartupReporter(
      logger,
      services: provider,
      coordinator: null!,
      orderedStream: Options.Create(new OrderedStreamProcessorOptions { ParallelizeStreams = false }),
      outboxDrain: Options.Create(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 128 }),
      inboxDispatch: Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 64 }));

    await reporter.StartAsync(CancellationToken.None);

    var warnings = logger.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("the coordinator options were supplied nowhere, so nothing can be said about outbox parallelism; "
        + "the inbox finding still stands");
    await Assert.That(warnings[0].Message).Contains("64");
    await Assert.That(warnings[0].Message.Contains("128", StringComparison.Ordinal)).IsFalse()
      .Because("reporting on options nobody supplied would be a false positive");
  }
}
