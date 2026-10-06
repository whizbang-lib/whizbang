// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// Branch coverage for <see cref="GuidOrderingValidator.ValidateForTimeOrdering"/>'s severity switch
/// when the configured severity is not one of the declared values (a value bound from configuration
/// as a raw number, say). An unrecognized severity must neither throw nor log: it falls through the
/// switch exactly as <see cref="GuidOrderingSeverity.None"/> does.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Validation/GuidOrderingValidator.cs</code-under-test>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingValidatorBranchCoverageTests {
  [Test]
  public async Task ValidateForTimeOrdering_UndeclaredSeverity_NeitherThrowsNorLogsAsync() {
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = (GuidOrderingSeverity)99
    };
    var logger = new RecordingLogger();
    var validator = new GuidOrderingValidator(options, logger);

    validator.ValidateForTimeOrdering(TrackedGuid.NewRandom(), "TestContext");

    await Assert.That(logger.Count).IsEqualTo(0)
      .Because("only Error, Warning and Info act on a violation; any other value is inert");
  }

  private sealed class RecordingLogger : ILogger {
    public int Count { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Count++;
  }
}
