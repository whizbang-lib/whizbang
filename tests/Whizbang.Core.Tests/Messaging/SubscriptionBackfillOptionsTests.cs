// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2092: the paced backfill's settings default to a pace that cannot flood a transport or a database, and a value
/// below one is rejected at host start naming its configuration key.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIntegrityOptionsValidator.cs</code-under-test>
public class SubscriptionBackfillOptionsTests {

  [Test]
  public async Task Defaults_PaceTheBackfillSoItCannotFloodAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.BackfillWindowSize).IsEqualTo(1_000)
      .Because("a window is a bounded slice of one type's history, well under an owner's 10,000-event answer cap");
    await Assert.That(options.BackfillEventsPerMinute).IsEqualTo(6_000)
      .Because("six windows a minute: a million-event type takes hours, not one burst");
    await Assert.That(options.BackfillMaxConcurrentWindows).IsEqualTo(2);
    await Assert.That(options.BackfillMaxWindowAttempts).IsEqualTo(3);
  }

  [Test]
  [Arguments("BackfillWindowSize")]
  [Arguments("BackfillEventsPerMinute")]
  [Arguments("BackfillMaxConcurrentWindows")]
  [Arguments("BackfillMaxWindowAttempts")]
  public async Task Validate_AValueBelowOne_FailsNamingTheKeyAsync(string key) {
    var options = new StreamIntegrityOptions();
    switch (key) {
      case "BackfillWindowSize":
        options.BackfillWindowSize = 0;
        break;
      case "BackfillEventsPerMinute":
        options.BackfillEventsPerMinute = 0;
        break;
      case "BackfillMaxConcurrentWindows":
        options.BackfillMaxConcurrentWindows = 0;
        break;
      default:
        options.BackfillMaxWindowAttempts = 0;
        break;
    }

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains($"Whizbang:StreamIntegrity:{key}");
  }
}
