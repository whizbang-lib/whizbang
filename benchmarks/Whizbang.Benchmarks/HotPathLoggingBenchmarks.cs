// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;

namespace Whizbang.Benchmarks;

/// <summary>
/// The cost of the dispatcher's per-dispatch tag-processing log calls while debug logging is off (#1756).
/// </summary>
/// <remarks>
/// <para>
/// The <c>Before_</c> methods are the calls the dispatcher made before #1756, verbatim: the
/// <c>LoggerExtensions.LogDebug(string, params object[])</c> overload, which builds its argument array (and boxes a
/// value-type argument) before the logger can say it is disabled. The <c>After_</c> methods are the
/// <c>[LoggerMessage]</c> source-generated replacements, which check the level first and take typed arguments.
/// </para>
/// <para>
/// The logger is a real one from <see cref="LoggerFactory"/> with an enabled provider and a minimum level of
/// Information, which is how a production host runs: debug is off by filter, not by a null logger.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MarkdownExporter]
public partial class HotPathLoggingBenchmarks {
  public record SampleCommand(int Value);

  private ILoggerFactory _factory = null!;
  private ILogger _logger = null!;
  private readonly Type _messageType = typeof(SampleCommand);
  private readonly TagProcessingMode _mode = TagProcessingMode.AsLifecycleStage;
  private readonly (Guid StreamId, string PerspectiveName, int Count)[] _groups =
    [.. Enumerable.Range(0, 16).Select(i => (Guid.NewGuid(), $"perspective-{i}", 2))];

  [GlobalSetup]
  public void Setup() {
    _factory = LoggerFactory.Create(builder => {
      builder.SetMinimumLevel(LogLevel.Information);
      builder.AddProvider(new EnabledNoOpProvider());
    });
    _logger = _factory.CreateLogger("Whizbang.Core.Dispatcher");
  }

  [GlobalCleanup]
  public void Cleanup() => _factory.Dispose();

  // The path every dispatch takes when a tag processor is registered: entry, before, after.
#pragma warning disable CA1848, CA1873, CA2254 // The point of the baseline is the un-generated call
  [Benchmark(Baseline = true)]
  public void Before_ProcessorPath() {
    _logger.LogDebug("ProcessTagsIfEnabledAsync called for {MessageType}", _messageType.Name);
    _logger.LogDebug("Calling ProcessTagsAsync at AfterReceptorCompletion...");
    _logger.LogDebug("ProcessTagsAsync completed");
  }

  [Benchmark]
  public void Before_LifecycleModePath() {
    _logger.LogDebug("ProcessTagsIfEnabledAsync called for {MessageType}", _messageType.Name);
    _logger.LogDebug("TagProcessingMode is {TagProcessingMode}, not AfterReceptorCompletion - returning early", _mode);
  }

  // The perspective worker's per-batch description: a header plus one line per (stream, perspective) group.
  [Benchmark]
  public void Before_BatchComposition16Groups() {
    _logger.LogDebug("Perspective channel batch: PerspectiveWork count: {WorkCount}, Grouped into {GroupCount} unique (StreamId, PerspectiveName) pairs",
      _groups.Length * 2, _groups.Length);
    foreach (var (streamId, perspectiveName, count) in _groups) {
      _logger.LogDebug("  - {PerspectiveName}/{StreamId}: {ItemCount} work items", perspectiveName, streamId, count);
    }
  }
#pragma warning restore CA1848, CA1873, CA2254

  [Benchmark]
  public void After_BatchComposition16Groups() {
    if (!_logger.IsEnabled(LogLevel.Debug)) {
      return;
    }
#pragma warning disable CA1848, CA1873, CA2254
    _logger.LogDebug("Perspective channel batch: PerspectiveWork count: {WorkCount}, Grouped into {GroupCount} unique (StreamId, PerspectiveName) pairs",
      _groups.Length * 2, _groups.Length);
    foreach (var (streamId, perspectiveName, count) in _groups) {
      _logger.LogDebug("  - {PerspectiveName}/{StreamId}: {ItemCount} work items", perspectiveName, streamId, count);
    }
  }
#pragma warning restore CA1848, CA1873, CA2254

  [Benchmark]
  public void After_ProcessorPath() {
    GeneratedLog.TagProcessingEntered(_logger, _messageType.Name);
    GeneratedLog.TagProcessingCalling(_logger);
    GeneratedLog.TagProcessingCompleted(_logger);
  }

  [Benchmark]
  public void After_LifecycleModePath() {
    GeneratedLog.TagProcessingEntered(_logger, _messageType.Name);
    GeneratedLog.TagProcessingModeSkipped(_logger, _mode);
  }

  // Mirrors the dispatcher's generated methods (same levels, templates and argument types).
  private static partial class GeneratedLog {
    [LoggerMessage(Level = LogLevel.Debug, Message = "ProcessTagsIfEnabledAsync called for {MessageType}")]
    public static partial void TagProcessingEntered(ILogger logger, string messageType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TagProcessingMode is {TagProcessingMode}, not AfterReceptorCompletion - returning early")]
    public static partial void TagProcessingModeSkipped(ILogger logger, TagProcessingMode tagProcessingMode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Calling ProcessTagsAsync at AfterReceptorCompletion...")]
    public static partial void TagProcessingCalling(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ProcessTagsAsync completed")]
    public static partial void TagProcessingCompleted(ILogger logger);
  }

  /// <summary>A provider that would write everything; the factory's minimum level is what turns debug off.</summary>
  private sealed class EnabledNoOpProvider : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new Sink();
    public void Dispose() { }

    private sealed class Sink : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) { }
    }
  }
}
