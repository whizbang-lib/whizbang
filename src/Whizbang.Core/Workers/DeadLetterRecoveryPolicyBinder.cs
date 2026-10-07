// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Binds <see cref="DeadLetterRecoveryOptions.PolicyByReason"/> from
/// <c>Whizbang:DeadLetterRecovery:PolicyByReason:&lt;Reason&gt;</c>.
/// </summary>
/// <remarks>
/// A <see cref="RecoveryPolicy"/> is a positional record, which the generated configuration binder
/// cannot build, so each entry is read field by field. An entry overrides only the fields it names on
/// that reason's existing policy, so one setting such as
/// <c>Whizbang__DeadLetterRecovery__PolicyByReason__Throttled__MaxRecoveryAttempts</c> changes one
/// thing. A reason with no existing policy starts from an empty one. A name that is not a
/// <see cref="MessageFailureReason"/>, and a value that does not parse, change nothing.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#deadletterrecoveryoptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryPolicyBindingTests.cs</tests>
internal static class DeadLetterRecoveryPolicyBinder {
  internal const string SECTION = "PolicyByReason";

  internal static void Bind(IConfiguration recoverySection, DeadLetterRecoveryOptions options) {
    foreach (var entry in recoverySection.GetSection(SECTION).GetChildren()) {
      // The fields are read before the reason is checked, so every key an entry can carry is read
      // whatever the reason is called (the configuration key manifest records them that way).
      var fields = _read(entry);
      if (!Enum.TryParse<MessageFailureReason>(entry.Key, ignoreCase: true, out var reason)
          || !Enum.IsDefined(reason)) {
        continue;
      }

      var policy = options.PolicyByReason.TryGetValue(reason, out var existing)
        ? existing
        : new RecoveryPolicy(string.Empty, 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: false);
      options.PolicyByReason[reason] = fields.ApplyTo(policy);
    }
  }

  private static EntryFields _read(IConfigurationSection entry) {
    var name = entry[nameof(RecoveryPolicy.Name)];
    return new EntryFields(
      string.IsNullOrWhiteSpace(name) ? null : name,
      int.TryParse(entry[nameof(RecoveryPolicy.MaxRecoveryAttempts)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts) ? attempts : null,
      TimeSpan.TryParse(entry[nameof(RecoveryPolicy.Cooldown)], CultureInfo.InvariantCulture, out var cooldown) ? cooldown : null,
      bool.TryParse(entry[nameof(RecoveryPolicy.HoldForReviewAfterExhaustion)], out var hold) ? hold : null);
  }

  /// <summary>The fields one entry sets; a null field is one the entry leaves alone.</summary>
  private readonly record struct EntryFields(string? Name, int? MaxRecoveryAttempts, TimeSpan? Cooldown, bool? HoldForReviewAfterExhaustion) {
    public RecoveryPolicy ApplyTo(RecoveryPolicy policy) => policy with {
      Name = Name ?? policy.Name,
      MaxRecoveryAttempts = MaxRecoveryAttempts ?? policy.MaxRecoveryAttempts,
      Cooldown = Cooldown ?? policy.Cooldown,
      HoldForReviewAfterExhaustion = HoldForReviewAfterExhaustion ?? policy.HoldForReviewAfterExhaustion
    };
  }
}
