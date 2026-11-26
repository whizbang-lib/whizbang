// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The write-behind bounds (#2247): required whenever anything writes behind, never defaulted to unlimited, and a flush
/// interval that leaves the lease room.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public class PerspectiveWriteBehindOptionsTests {
  private static PerspectiveWriteBehindOptions _bounded() => new() { MaxModels = 100, MaxBytes = 1_000_000 };

  [Test]
  public async Task TheBounds_HaveNoDefault_AndTheIntervalAndBatchCapDoAsync() {
    var options = new PerspectiveWorkerOptions().WriteBehind;

    await Assert.That(options.MaxModels).IsNull().Because("there is no unlimited default");
    await Assert.That(options.MaxBytes).IsNull();
    await Assert.That(options.MaxFlushIntervalMilliseconds).IsEqualTo(1000);
    await Assert.That(options.MaxBatchesPerModel).IsEqualTo(32);
  }

  [Test]
  public async Task NothingWritingBehind_NeedsNoBoundsAsync() {
    await Assert.That(PerspectiveWriteBehindOptions.Validate(new(), PerspectiveWriteMode.SetBased, [], 300, 30)).IsNull();
  }

  [Test]
  public async Task TheServiceMode_WithoutBounds_NamesBothMissingBoundsAndTheFixAsync() {
    var error = PerspectiveWriteBehindOptions.Validate(new(), PerspectiveWriteMode.WriteBehind, [], 300, 30);

    await Assert.That(error).IsNotNull();
    await Assert.That(error!).Contains("the service's perspective write mode is WriteBehind");
    await Assert.That(error).Contains("MaxModels is not set");
    await Assert.That(error).Contains("MaxBytes is not set");
    await Assert.That(error).Contains("Whizbang:Workers:Perspective:WriteBehind:MaxModels");
  }

  [Test]
  public async Task ADeclaredPerspective_WithoutBounds_IsNamedAsync() {
    var error = PerspectiveWriteBehindOptions.Validate(
      new() { MaxModels = 10 }, PerspectiveWriteMode.SetBased, ["App.Counters"], 300, 30);

    await Assert.That(error!).Contains("App.Counters");
    await Assert.That(error!).Contains("MaxBytes is not set");
    await Assert.That(error!).DoesNotContain("MaxModels is not set");
  }

  [Test]
  public async Task BoundsThatAreNotPositive_AreRefusedAsync() {
    var error = PerspectiveWriteBehindOptions.Validate(
      new() { MaxModels = 0, MaxBytes = -1, MaxBatchesPerModel = 0 }, PerspectiveWriteMode.WriteBehind, [], 300, 30);

    await Assert.That(error!).Contains("MaxModels is not set to a positive number");
    await Assert.That(error!).Contains("MaxBytes is not set to a positive number");
    await Assert.That(error!).Contains("MaxBatchesPerModel is below 1");
  }

  [Test]
  public async Task AnIntervalBeyondHalfTheLeaseLessItsGrace_IsRefusedAsync() {
    var atLimit = _bounded();
    atLimit.MaxFlushIntervalMilliseconds = 135_000;
    var over = _bounded();
    over.MaxFlushIntervalMilliseconds = 135_001;
    var zero = _bounded();
    zero.MaxFlushIntervalMilliseconds = 0;

    await Assert.That(PerspectiveWriteBehindOptions.Validate(atLimit, PerspectiveWriteMode.WriteBehind, [], 300, 30)).IsNull();
    await Assert.That(PerspectiveWriteBehindOptions.Validate(over, PerspectiveWriteMode.WriteBehind, [], 300, 30)!)
      .Contains("half of the lease (300 s) less its grace (30 s)");
    await Assert.That(PerspectiveWriteBehindOptions.Validate(zero, PerspectiveWriteMode.WriteBehind, [], 300, 30)).IsNotNull();
    await Assert.That(PerspectiveWriteBehindOptions.Validate(_bounded(), PerspectiveWriteMode.WriteBehind, [], 10, 30)).IsNotNull()
      .Because("a grace longer than the lease leaves no room to hold anything");
  }

  [Test]
  public async Task ArgumentsAreRequiredAsync() {
    await Assert.That(() => PerspectiveWriteBehindOptions.Validate(null!, PerspectiveWriteMode.SetBased, [], 300, 30))
      .Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveWriteBehindOptions.Validate(new(), PerspectiveWriteMode.SetBased, null!, 300, 30))
      .Throws<ArgumentNullException>();
  }
}
