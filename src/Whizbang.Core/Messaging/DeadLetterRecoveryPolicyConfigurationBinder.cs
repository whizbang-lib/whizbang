// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Messaging;

/// <summary>
/// AOT-safe configuration binder for <see cref="DeadLetterRecoveryOptions.PolicyByReason"/>. Each child of
/// <c>Whizbang:DeadLetterRecovery:PolicyByReason</c> is a <see cref="MessageFailureReason"/> whose keys
/// (<c>Name</c>, <c>MaxRecoveryAttempts</c>, <c>Cooldown</c>, <c>HoldForReviewAfterExhaustion</c>) override that
/// reason's existing policy.
/// </summary>
/// <remarks>
/// <para>
/// The generated binder cannot do this. <see cref="RecoveryPolicy"/> is a positional record, so the binder needs
/// every constructor argument present; naming one field threw at startup with "parameter 'Cooldown' has no
/// matching config", which meant the documented key could not be set at all. Binding field by field also gives
/// the behaviour an operator expects from an override: the fields it does not name keep the shipped default for
/// that reason, rather than silently resetting to zero.
/// </para>
/// <para>
/// Nothing here throws. A reason that is not a <see cref="MessageFailureReason"/>, or a value that does not
/// parse, leaves the policy as it was — a typo in one entry must not stop the service from starting, which is
/// the failure this replaces.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DeadLetterRecoveryPolicyConfigurationBinderTests.cs</tests>
internal static class DeadLetterRecoveryPolicyConfigurationBinder {
  /// <summary>Configuration section the per-reason policies bind from.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:DeadLetterRecovery:PolicyByReason";

  /// <summary>
  /// Applies every configured per-reason policy onto <paramref name="options"/>. No-ops when
  /// <paramref name="configuration"/> is null or the section is absent.
  /// </summary>
  /// <param name="options">The options whose policy map is updated.</param>
  /// <param name="configuration">Configuration to read the section from.</param>
  internal static void Apply(DeadLetterRecoveryOptions options, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(options);

    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section?.Exists() != true) {
      return;
    }

    foreach (var entry in section.GetChildren()) {
      if (!Enum.TryParse<MessageFailureReason>(entry.Key, ignoreCase: true, out var reason)
          || !Enum.IsDefined(reason)) {
        continue;
      }

      // A reason the defaults do not cover starts from an empty policy rather than being skipped, so an
      // operator can introduce one from configuration alone.
      var policy = options.PolicyByReason.TryGetValue(reason, out var existing)
        ? existing
        : new RecoveryPolicy(string.Empty, 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: false);

      ConfigurationValueBinder.BindString(entry, nameof(RecoveryPolicy.Name),
        v => policy = policy with { Name = v });
      ConfigurationValueBinder.BindInt(entry, nameof(RecoveryPolicy.MaxRecoveryAttempts),
        v => policy = policy with { MaxRecoveryAttempts = v });
      ConfigurationValueBinder.BindTimeSpan(entry, nameof(RecoveryPolicy.Cooldown),
        v => policy = policy with { Cooldown = v });
      ConfigurationValueBinder.BindBool(entry, nameof(RecoveryPolicy.HoldForReviewAfterExhaustion),
        v => policy = policy with { HoldForReviewAfterExhaustion = v });

      options.UsePolicy(reason, policy);
    }
  }
}
