// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// Chooses the level a dead-lettered message's record (<see cref="Observability.DeadLetterLog"/>) is logged at, on
/// every path that dead-letters: the inbox, the outbox, the perspective worker, the transport consumer, the batchers'
/// flush failure recorders and the broker dead-letter drains. Bound from <c>Whizbang:DeadLetterLogging</c>.
/// </summary>
/// <remarks>
/// <para>A dead-lettered message will not be processed unless it is recovered, so its record defaults to
/// <see cref="LogLevel.Error"/>, where a consumer that alerts on Error hears about it. A consumer whose alerting
/// should not fire on dead letters sets <see cref="LogLevel.Warning"/>, the level the framework used before.</para>
/// <para>Only those two levels are accepted, and any other is refused when the host starts
/// (<see cref="DeadLetterLoggingOptionsValidator"/>). A lower level would put a message that will never succeed
/// below the Warnings its retried attempts log, <see cref="LogLevel.None"/> would hide it, and
/// <see cref="LogLevel.Critical"/> is kept for failures that put the host itself at risk. Refusing rather than
/// clamping means a setting that does not take effect is never mistaken for one that does.</para>
/// <para>A failure that will be retried is not a dead letter and keeps its own level (Warning on the inbox and
/// outbox dispatch paths).</para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#dead-letter-logging</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingOptionsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingWiringTests.cs</tests>
public sealed class DeadLetterLoggingOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:DeadLetterLogging";

  /// <summary>
  /// The level a dead-lettered message's record is logged at: <see cref="LogLevel.Error"/> (the default) or
  /// <see cref="LogLevel.Warning"/>. Any other level is refused at startup.
  /// </summary>
  /// <docs>operations/configuration/configuration-reference#dead-letter-logging</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingOptionsTests.cs:Default_IsErrorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingOptionsTests.cs:Turnkey_LevelWarning_BindsFromConfigurationAsync</tests>
  public LogLevel Level { get; set; } = LogLevel.Error;
}

/// <summary>
/// Refuses a <see cref="DeadLetterLoggingOptions.Level"/> other than <see cref="LogLevel.Warning"/> or
/// <see cref="LogLevel.Error"/>, naming the configuration key and the levels it accepts. Registered with the options
/// and checked when the host starts.
/// </summary>
/// <docs>operations/configuration/configuration-reference#dead-letter-logging</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingOptionsTests.cs:Validator_RefusesEveryOtherLevel_NamingTheKeyAndTheChoicesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterLoggingOptionsTests.cs:Turnkey_UnsupportedLevel_IsRefusedAtStartupAsync</tests>
public sealed class DeadLetterLoggingOptionsValidator : IValidateOptions<DeadLetterLoggingOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, DeadLetterLoggingOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return options.Level is LogLevel.Warning or LogLevel.Error
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(
        $"{DeadLetterLoggingOptions.SECTION}:{nameof(DeadLetterLoggingOptions.Level)} is {options.Level}; "
        + $"a dead-lettered message is logged at {nameof(LogLevel.Error)} (the default) or {nameof(LogLevel.Warning)}, and no other level is accepted.");
  }
}
