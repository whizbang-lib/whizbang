// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Rejects notification windows of zero or less where the notification options are read, naming each wrong key:
/// a durable-signal retention window (#1574), which would make every signal a pod has already delivered eligible at
/// once, and a LISTEN keepalive interval (#2155), which is also the timeout of the LISTEN connection's wait and which
/// Npgsql would read as no timeout at all, removing the bound on a lost wake-up and on a dead connection.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus#retention</docs>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs:AddWhizbangPostgresNotifications_ANonPositiveSignalRetention_IsRejectedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs:AddWhizbangPostgresNotifications_ANonPositiveListenKeepaliveInterval_IsRejectedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs:AddWhizbangPostgresNotifications_BothWindowsNonPositive_ReportsEachKeyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs:AddWhizbangPostgresNotifications_TheDefaults_AreAcceptedAsync</tests>
public sealed class WhizbangNotificationOptionsValidator : IValidateOptions<WhizbangNotificationOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, WhizbangNotificationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    _requirePositive(failures, nameof(WhizbangNotificationOptions.SignalRetention), options.SignalRetention);
    _requirePositive(failures, nameof(WhizbangNotificationOptions.ListenKeepaliveInterval), options.ListenKeepaliveInterval);
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }

  private static void _requirePositive(List<string> failures, string key, TimeSpan value) {
    if (value <= TimeSpan.Zero) {
      failures.Add(string.Create(CultureInfo.InvariantCulture,
        $"{PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION}:{key} must be greater than zero; got {value}."));
    }
  }
}
