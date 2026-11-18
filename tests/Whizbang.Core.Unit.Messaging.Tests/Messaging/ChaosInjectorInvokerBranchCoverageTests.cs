// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="ChaosInjectorInvoker"/>'s flag read when the options wrapper is
/// present but holds no value: that must read as "chaos hooks off", the production-safe default,
/// even with a real injector registered.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ChaosInjectorInvoker.cs</code-under-test>
public class ChaosInjectorInvokerBranchCoverageTests {

  private sealed class EmptyOptions : IOptions<WhizbangOptions> {
    public WhizbangOptions Value => null!;
  }

  private sealed class CountingInjector : IChaosInjector {
    public int CallCount { get; private set; }

    public ValueTask BeforeCheckpointAsync(string checkpoint, object? payload, CancellationToken cancellationToken) {
      CallCount++;
      return ValueTask.CompletedTask;
    }

    public bool IsInjecting => true;
  }

  [Test]
  public async Task OptionsWrapperWithNoValue_IsInactiveAndNeverCallsTheInjectorAsync() {
    var injector = new CountingInjector();
    var sut = new ChaosInjectorInvoker(new EmptyOptions(), injector);

    await sut.BeforeCheckpointAsync("Worker.Checkpoint", payload: null, CancellationToken.None);

    await Assert.That(sut.IsActive).IsFalse()
      .Because("an options wrapper without a value carries no opt-in, so chaos stays off");
    await Assert.That(injector.CallCount).IsEqualTo(0)
      .Because("with chaos off, a registered injector must never be reached on the hot path");
  }
}
