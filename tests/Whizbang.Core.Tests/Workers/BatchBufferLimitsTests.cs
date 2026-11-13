// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The limits the batch buffer runs with (#2220): a byte limit that is a set number when one is configured and a share
/// of the process memory limit otherwise, a fixed message limit, the gate's interactive reserve fraction and the
/// transaction ceiling; each derived value carries its inputs to the startup settings report.
/// </summary>
public class BatchBufferLimitsTests {
  private const long MEMORY = 2_000_000_000;

  [Test]
  public async Task TheDefaults_AreTenThousandMessages_AndFivePercentOfMemoryAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions(), MEMORY, gateMaxConcurrent: 50, gateInteractiveReserve: 5);

    await Assert.That(limits.MaxMessages).IsEqualTo(10_000);
    await Assert.That(limits.MaxBytes).IsEqualTo(100_000_000);
    await Assert.That(limits.InteractiveReserveFraction).IsEqualTo(0.1);
    await Assert.That(limits.TransactionCeilingMultiplier).IsEqualTo(2.0);
  }

  [Test]
  public async Task TheDerivedValues_CarryTheirInputs_ForTheStartupSettingsReportAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions(), MEMORY, gateMaxConcurrent: 50, gateInteractiveReserve: 5);

    var derived = limits.DerivedSettings.Select(d => d.ToString()!).ToList();

    await Assert.That(derived).IsEquivalentTo([
      "100000000 (0.05 x TotalAvailableMemoryBytes; TotalAvailableMemoryBytes=2000000000)",
      "0.1 (WorkCoordinatorGate InteractiveReserve / MaxConcurrent; InteractiveReserve=5, MaxConcurrent=50)",
    ]);
    await Assert.That(limits.DerivedSettings.First().Name).IsEqualTo("Whizbang:BatchBuffer:MaxBufferedBytes");
  }

  [Test]
  public async Task ASetByteLimit_WinsOverTheFraction_AndIsNotReportedAsDerivedAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedBytes = 4096, MaxBufferedBytesFraction = 0.5 }, MEMORY, 50, 5);

    await Assert.That(limits.MaxBytes).IsEqualTo(4096);
    await Assert.That(limits.DerivedSettings.Any(d => d.Name.EndsWith("MaxBufferedBytes", StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  [Arguments(0L, 0.05)]
  [Arguments(-1L, 0.05)]
  [Arguments(null, 0.0)]
  public async Task AByteLimitOfZeroOrLess_OrNoFraction_MeansNoByteLimitAsync(long? set, double fraction) {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedBytes = set, MaxBufferedBytesFraction = fraction }, MEMORY, 50, 5);

    await Assert.That(limits.MaxBytes).IsNull();
  }

  [Test]
  public async Task AMessageLimitOfZeroOrLess_MeansNoMessageLimitAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedMessages = 0 }, MEMORY, 50, 5);

    await Assert.That(limits.MaxMessages).IsNull();
  }

  [Test]
  public async Task AFractionOfAVerySmallMemoryLimit_IsAtLeastOneByteAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions(), totalAvailableMemoryBytes: 10, 50, 5);

    await Assert.That(limits.MaxBytes).IsEqualTo(1);
  }

  [Test]
  public async Task ACeilingBelowOne_IsOne_AndADisabledGate_ReservesNothingAsync() {
    var limits = BatchBufferLimits.Resolve(new BatchBufferOptions { TransactionCeilingMultiplier = 0.5 }, MEMORY, gateMaxConcurrent: 0, gateInteractiveReserve: 0);

    await Assert.That(limits.TransactionCeilingMultiplier).IsEqualTo(1.0);
    await Assert.That(limits.InteractiveReserveFraction).IsEqualTo(0.0);
  }

  [Test]
  public async Task Resolve_RefusesMissingOptionsAsync() {
    await Assert.That(() => BatchBufferLimits.Resolve(null!, MEMORY, 50, 5)).Throws<ArgumentNullException>();
  }
}
