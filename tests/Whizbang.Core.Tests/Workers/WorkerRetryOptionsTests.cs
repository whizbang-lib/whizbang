// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>The backoff a retry actually uses: the multiplier when exponential backoff is on, else a fixed timeout.</summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerRetryOptions.cs</code-under-test>
public class WorkerRetryOptionsTests {
  [Test]
  public async Task Effective_BackoffOn_IsTheMultiplierAsync() {
    var options = new WorkerRetryOptions { BackoffMultiplier = 3.0 };

    await Assert.That(options.EffectiveBackoffMultiplier).IsEqualTo(3.0);
  }

  [Test]
  public async Task Effective_BackoffOff_IsOneAsync() {
    var options = new WorkerRetryOptions { EnableExponentialBackoff = false, BackoffMultiplier = 3.0 };

    await Assert.That(options.EffectiveBackoffMultiplier).IsEqualTo(1.0)
      .Because("with backoff off every retry waits the same fixed timeout");
  }
}
