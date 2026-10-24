// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Issue #1677: the last check before a generated runner writes a perspective row. A model no Apply produced is the
/// scaffolded default built as Apply input; the guard refuses to store it and says so.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
public class PerspectiveRowWriteGuardTests {
  [Test]
  public async Task AllowsWrite_AModelAnApplyProduced_IsWrittenAndNothingIsLoggedAsync() {
    var logger = new RecordingLogger();

    var allowed = PerspectiveRowWriteGuard.AllowsWrite(producedByApply: true, logger, "OrderPerspective", Guid.NewGuid());

    await Assert.That(allowed).IsTrue();
    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task AllowsWrite_AModelNoApplyProduced_IsRefusedWithAWarningNamingTheRowAsync() {
    var logger = new RecordingLogger();
    var streamId = Guid.NewGuid();

    var allowed = PerspectiveRowWriteGuard.AllowsWrite(producedByApply: false, logger, "OrderPerspective", streamId);

    await Assert.That(allowed).IsFalse()
      .Because("storing the scaffolded default would create a blank row that reads as a real one");
    var (level, message) = logger.Entries.Single();
    await Assert.That(level).IsEqualTo(LogLevel.Warning);
    await Assert.That(message).Contains("OrderPerspective");
    await Assert.That(message).Contains(streamId.ToString());
    await Assert.That(message).Contains("[diag.write.unproduced]");
  }

  [Test]
  public async Task AllowsWrite_WithNoLogger_ThrowsAsync() {
    await Assert.That(() => PerspectiveRowWriteGuard.AllowsWrite(true, null!, "OrderPerspective", Guid.NewGuid()))
      .ThrowsExactly<ArgumentNullException>();
  }

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }
}
