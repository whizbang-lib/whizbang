// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="DeadLetterRecoveryOptions.PolicyByReason"/> binds from
/// <c>Whizbang:DeadLetterRecovery:PolicyByReason:&lt;Reason&gt;</c>. A policy is a positional record,
/// which the generated binder cannot build, so each entry is read field by field and overrides only
/// the fields it names on that reason's existing policy.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryPolicyBinder.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#dead-letter-recovery</docs>
public class DeadLetterRecoveryPolicyBindingTests {
  private static DeadLetterRecoveryOptions _bound(params (string Key, string Value)[] settings) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(settings.ToDictionary(
        s => "Whizbang:DeadLetterRecovery:PolicyByReason:" + s.Key, s => (string?)s.Value))
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider().GetRequiredService<IOptions<DeadLetterRecoveryOptions>>().Value;
  }

  [Test]
  public async Task AnEntry_ReplacesThatReasonsPolicyAsync() {
    var options = _bound(
      ("Throttled:Name", "Patient"),
      ("Throttled:MaxRecoveryAttempts", "9"),
      ("Throttled:Cooldown", "02:00:00"),
      ("Throttled:HoldForReviewAfterExhaustion", "true"));

    await Assert.That(options.PolicyByReason[MessageFailureReason.Throttled])
      .IsEqualTo(new RecoveryPolicy("Patient", 9, TimeSpan.FromHours(2), HoldForReviewAfterExhaustion: true));
  }

  [Test]
  public async Task APartialEntry_KeepsTheFieldsItDoesNotNameAsync() {
    var original = new DeadLetterRecoveryOptions().PolicyByReason[MessageFailureReason.TransportException];

    var options = _bound(("TransportException:MaxRecoveryAttempts", "7"));

    await Assert.That(options.PolicyByReason[MessageFailureReason.TransportException])
      .IsEqualTo(original with { MaxRecoveryAttempts = 7 });
  }

  /// <summary>Reason names match the way enum names are written in configuration: ignoring case.</summary>
  [Test]
  public async Task AReasonName_MatchesIgnoringCaseAsync() {
    var options = _bound(("leaseexpired:Cooldown", "00:05:00"));

    await Assert.That(options.PolicyByReason[MessageFailureReason.LeaseExpired].Cooldown)
      .IsEqualTo(TimeSpan.FromMinutes(5));
  }

  /// <summary>A reason that is not one leaves every policy as it was rather than inventing one.</summary>
  [Test]
  public async Task AnUnknownReason_ChangesNothingAsync() {
    var defaults = new DeadLetterRecoveryOptions().PolicyByReason;

    var options = _bound(("NotAReason:MaxRecoveryAttempts", "4"));

    await Assert.That(options.PolicyByReason).IsEquivalentTo(defaults);
  }

  [Test]
  public async Task UnreadableFieldValues_KeepThoseFieldsAsync() {
    var original = new DeadLetterRecoveryOptions().PolicyByReason[MessageFailureReason.Unknown];

    var options = _bound(
      ("Unknown:MaxRecoveryAttempts", "several"),
      ("Unknown:Cooldown", "a while"),
      ("Unknown:HoldForReviewAfterExhaustion", "maybe"),
      ("Unknown:Name", ""));

    await Assert.That(options.PolicyByReason[MessageFailureReason.Unknown]).IsEqualTo(original);
  }

  /// <summary>A reason with no default policy gets one from its entry, defaults filling the rest.</summary>
  [Test]
  public async Task AReasonWithoutADefault_GetsThePolicyItsEntryDescribesAsync() {
    await Assert.That(new DeadLetterRecoveryOptions().PolicyByReason.ContainsKey(MessageFailureReason.None)).IsFalse()
      .Because("the case needs a reason with no default policy");

    var bound = _bound(("None:Name", "Custom"), ("None:MaxRecoveryAttempts", "2"));

    await Assert.That(bound.PolicyByReason[MessageFailureReason.None])
      .IsEqualTo(new RecoveryPolicy("Custom", 2, TimeSpan.Zero, HoldForReviewAfterExhaustion: false));
  }
}
