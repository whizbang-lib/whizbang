// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests;

/// <summary>
/// Every assembly's startup callback runs: each slot a generated module initializer sets accumulates instead of
/// keeping only the last assembly's (#2198 raw receptors and pinned identities, #2199 lenses and perspective services).
/// </summary>
/// <remarks>
/// The slots are process-wide and filled by module initializers before any test runs, so each test saves the slot's
/// value, clears it, and puts the saved value back after clearing again: assigning accumulates, so restoring without
/// clearing first would leave the test's callbacks behind.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/ServiceRegistrationCallbacks.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceRegistrationCallbacksAccumulateTests {
  private static async Task _everyCallbackRunsAsync<TCallback>(
      Func<TCallback?> get, Action<TCallback?> set, Func<List<string>, string, TCallback> callback)
      where TCallback : Delegate {
    var saved = get();
    try {
      set(null);
      var order = new List<string>();
      set(callback(order, "Orders"));
      set(callback(order, "Billing"));

      ServiceRegistrationCallbacks.InvokeAll(new ServiceCollection(), new ServiceRegistrationOptions());

      await Assert.That(order.SequenceEqual(["Orders", "Billing"])).IsTrue()
        .Because($"every assembly's callback runs, in the order set, not only the last one; ran: {string.Join(", ", order)}");

      set(null);
      await Assert.That(get()).IsNull().Because("assigning null clears every callback");
    } finally {
      set(null);
      set(saved);
    }
  }

  [Test]
  public async Task RawReceptors_RunsEveryAssemblysCallback_InTheOrderSetAsync() =>
    await _everyCallbackRunsAsync(
      () => ServiceRegistrationCallbacks.RawReceptors, v => ServiceRegistrationCallbacks.RawReceptors = v,
      static (order, name) => _ => order.Add(name));

  [Test]
  public async Task PinnedIdRegistry_RunsEveryAssemblysCallback_InTheOrderSetAsync() =>
    await _everyCallbackRunsAsync(
      () => ServiceRegistrationCallbacks.PinnedIdRegistry, v => ServiceRegistrationCallbacks.PinnedIdRegistry = v,
      static (order, name) => _ => order.Add(name));
}
