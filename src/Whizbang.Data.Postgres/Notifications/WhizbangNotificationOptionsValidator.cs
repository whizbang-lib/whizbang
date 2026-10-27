// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Rejects a durable-signal retention window of zero or less where the notification options are read (#1574): such a
/// window would make every signal a pod has already delivered eligible at once.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus#retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs:AddWhizbangPostgresNotifications_ANonPositiveSignalRetention_IsRejectedAsync</tests>
public sealed class WhizbangNotificationOptionsValidator : IValidateOptions<WhizbangNotificationOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, WhizbangNotificationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return options.SignalRetention > TimeSpan.Zero
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(string.Create(CultureInfo.InvariantCulture,
          $"{PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION}:{nameof(WhizbangNotificationOptions.SignalRetention)} must be greater than zero; got {options.SignalRetention}."));
  }
}
