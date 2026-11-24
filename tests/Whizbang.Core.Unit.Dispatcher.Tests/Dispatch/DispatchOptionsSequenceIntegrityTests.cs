// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>The per-dispatch sequence integrity setting, the most specific level of its precedence.</summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DispatchOptions.cs</code-under-test>
[Category("Unit")]
public class DispatchOptionsSequenceIntegrityTests {
  [Test]
  public async Task ByDefault_TheDispatchLeavesTheTypeAndTheConfiguredDefaultInChargeAsync() {
    var options = new DispatchOptions();

    await Assert.That(options.SequenceIntegrity).IsNull();
    await Assert.That(options.SequenceHoldTime).IsNull();
  }

  [Test]
  public async Task WithSequenceIntegrity_SetsTheMode_AndTheHoldTimeOnlyWhenGivenAsync() {
    var options = new DispatchOptions();

    var returned = options.WithSequenceIntegrity(SequenceIntegrity.UntilStamped);

    await Assert.That(returned).IsSameReferenceAs(options);
    await Assert.That(options.SequenceIntegrity).IsEqualTo(SequenceIntegrity.UntilStamped);
    await Assert.That(options.SequenceHoldTime).IsNull();

    options.WithSequenceIntegrity(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(250));

    await Assert.That(options.SequenceIntegrity).IsEqualTo(SequenceIntegrity.Bounded);
    await Assert.That(options.SequenceHoldTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
  }

  [Test]
  public async Task WithSequenceIntegrity_ANegativeHoldTime_IsRefusedAsync() {
    var options = new DispatchOptions();

    await Assert.That(() => options.WithSequenceIntegrity(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(-1)))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(options.SequenceIntegrity).IsNull()
      .Because("a refused call changes nothing");
  }
}
