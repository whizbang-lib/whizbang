// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <c>Whizbang:DeadLetterRecovery:PolicyByReason:&lt;Reason&gt;:&lt;Field&gt;</c> binds field by field, so an
/// operator can override one field of one reason.
/// </summary>
/// <remarks>
/// The generated binder could not: <see cref="RecoveryPolicy"/> is a positional record and the binder needs
/// every constructor argument, so naming a single field threw at startup and the documented key was unusable.
/// These cases pin both halves of the replacement — the override applies, and the fields it does not name keep
/// the shipped default rather than resetting to zero.
/// </remarks>
public class DeadLetterRecoveryPolicyConfigurationBinderTests {

  private static DeadLetterRecoveryOptions _bind(params (string Key, string Value)[] settings) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s =>
        new KeyValuePair<string, string?>($"Whizbang:DeadLetterRecovery:PolicyByReason:{s.Key}", s.Value)))
      .Build();
    var options = new DeadLetterRecoveryOptions();
    DeadLetterRecoveryPolicyConfigurationBinder.Apply(options, configuration);
    return options;
  }

  [Test]
  public async Task Apply_OneFieldOfOneReason_OverridesOnlyThatFieldAsync() {
    var shipped = new DeadLetterRecoveryOptions().PolicyByReason[MessageFailureReason.Throttled];

    var options = _bind(("Throttled:MaxRecoveryAttempts", "7"));
    var policy = options.PolicyByReason[MessageFailureReason.Throttled];

    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(7);
    await Assert.That(policy.Name).IsEqualTo(shipped.Name)
      .Because("an override names one field; the rest must keep the shipped default rather than reset to the "
        + "type's zero, which is what a positional-record bind would have produced.");
    await Assert.That(policy.Cooldown).IsEqualTo(shipped.Cooldown);
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsEqualTo(shipped.HoldForReviewAfterExhaustion);
  }

  [Test]
  public async Task Apply_EveryFieldKind_BindsAsync() {
    var options = _bind(
      ("TransportException:Name", "Custom"),
      ("TransportException:MaxRecoveryAttempts", "9"),
      ("TransportException:Cooldown", "00:02:30"),
      ("TransportException:HoldForReviewAfterExhaustion", "true"));
    var policy = options.PolicyByReason[MessageFailureReason.TransportException];

    await Assert.That(policy.Name).IsEqualTo("Custom");
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(9);
    await Assert.That(policy.Cooldown).IsEqualTo(TimeSpan.FromSeconds(150));
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task Apply_AnUnknownReason_ChangesNothingAndDoesNotThrowAsync() {
    var before = new DeadLetterRecoveryOptions().PolicyByReason.Count;

    var options = _bind(("NotAFailureReason:MaxRecoveryAttempts", "4"));

    await Assert.That(options.PolicyByReason.Count).IsEqualTo(before)
      .Because("a typo in one entry must not add a phantom reason, and above all must not stop the service "
        + "starting -- throwing on it is the defect this replaces.");
  }

  [Test]
  public async Task Apply_AnUnparseableValue_LeavesTheFieldAloneAsync() {
    var shipped = new DeadLetterRecoveryOptions().PolicyByReason[MessageFailureReason.Throttled];

    var options = _bind(("Throttled:MaxRecoveryAttempts", "not-a-number"), ("Throttled:Cooldown", "later"));
    var policy = options.PolicyByReason[MessageFailureReason.Throttled];

    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(shipped.MaxRecoveryAttempts);
    await Assert.That(policy.Cooldown).IsEqualTo(shipped.Cooldown)
      .Because("an unreadable value is not an instruction to zero the field.");
  }

  [Test]
  public async Task Apply_AReasonWithNoShippedDefault_StartsFromAnEmptyPolicyAsync() {
    // Whichever reasons ship without a default, configuration alone must be able to introduce one.
    var shipped = new DeadLetterRecoveryOptions().PolicyByReason;
    var missing = Enum.GetValues<MessageFailureReason>().FirstOrDefault(r => !shipped.ContainsKey(r));
    if (shipped.Count == Enum.GetValues<MessageFailureReason>().Length) {
      await Assert.That(shipped.Count).IsGreaterThan(0);
      return;
    }

    var options = _bind(($"{missing}:MaxRecoveryAttempts", "2"));
    var policy = options.PolicyByReason[missing];

    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(2);
    await Assert.That(policy.Name).IsEqualTo(string.Empty)
      .Because("there was no policy to merge over, so the unnamed fields are empty rather than invented.");
  }

  [Test]
  public async Task Apply_TheReasonNameIsCaseInsensitiveAsync() {
    var options = _bind(("throttled:MaxRecoveryAttempts", "5"));

    await Assert.That(options.PolicyByReason[MessageFailureReason.Throttled].MaxRecoveryAttempts).IsEqualTo(5)
      .Because("configuration keys are routinely written in a different case than the enum declares.");
  }

  [Test]
  public async Task Apply_NoSectionOrNoConfiguration_LeavesTheShippedDefaultsAsync() {
    var shipped = new DeadLetterRecoveryOptions();
    var viaNull = new DeadLetterRecoveryOptions();
    DeadLetterRecoveryPolicyConfigurationBinder.Apply(viaNull, configuration: null);
    var viaEmpty = new DeadLetterRecoveryOptions();
    DeadLetterRecoveryPolicyConfigurationBinder.Apply(viaEmpty, new ConfigurationBuilder().Build());

    await Assert.That(viaNull.PolicyByReason.Count).IsEqualTo(shipped.PolicyByReason.Count);
    await Assert.That(viaEmpty.PolicyByReason.Count).IsEqualTo(shipped.PolicyByReason.Count);
  }

  [Test]
  public async Task PolicyByReason_HasNoPublicSetter_SoTheGeneratedBinderSkipsItAsync() {
    // This is the lock. The crash was the generated binder reaching PolicyByReason at all: it compiles
    // Bind into typed assignments, and assigning a Dictionary<_, RecoveryPolicy> means constructing a
    // positional record from a section that named one field. A property it cannot assign is a property it
    // does not generate for, so the absence of a public setter is what keeps the section away from it.
    // Giving the property a setter again brings the startup failure back, and fails here first.
    var property = typeof(DeadLetterRecoveryOptions).GetProperty(nameof(DeadLetterRecoveryOptions.PolicyByReason));

    await Assert.That(property).IsNotNull();
    await Assert.That(property!.SetMethod).IsNull()
      .Because("a settable PolicyByReason is what threw at startup; UsePolicy is the one writer.");
    await Assert.That(property.PropertyType.IsAssignableTo(typeof(System.Collections.IDictionary))).IsFalse()
      .Because("the exposed type is read-only too, so an entry cannot be added past UsePolicy.");
  }

  [Test]
  public async Task TheStartupPath_APartialEntry_BindsWithoutThrowingAsync() {
    // The composition startup uses: a Bind of the whole "Whizbang:DeadLetterRecovery" section followed by
    // the per-reason pass. A smoke test of the order, not a reproduction of the crash — the reflection
    // binder reachable from a test assembly tolerates the partial entry that the generated binder did not.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection([
        new KeyValuePair<string, string?>("Whizbang:DeadLetterRecovery:Enabled", "true"),
        new KeyValuePair<string, string?>(
          "Whizbang:DeadLetterRecovery:PolicyByReason:Throttled:MaxRecoveryAttempts", "9"),
      ])
      .Build();
    var options = new DeadLetterRecoveryOptions();

    ConfigurationBinder.Bind(configuration.GetSection("Whizbang:DeadLetterRecovery"), options);
    DeadLetterRecoveryPolicyConfigurationBinder.Apply(options, configuration);

    await Assert.That(options.PolicyByReason[MessageFailureReason.Throttled].MaxRecoveryAttempts).IsEqualTo(9)
      .Because("the documented key must take effect through the same two calls startup makes, not only "
        + "through a direct call to the per-reason binder.");
  }
}
