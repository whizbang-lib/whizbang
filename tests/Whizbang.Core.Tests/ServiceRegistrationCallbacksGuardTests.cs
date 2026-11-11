// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests;

/// <summary>
/// The guard on the startup slots (#2201): every slot a generated module initializer sets keeps every assembly's
/// callback, so a new slot cannot quietly keep only the last assembly's (#2198, #2199).
/// </summary>
/// <remarks>
/// It finds the slots by reflection over <see cref="ServiceRegistrationCallbacks"/>, so a slot added later is checked
/// without anyone listing it here. Each slot is saved, cleared, given two callbacks, read back and invoked; both must
/// run. Restoring clears first, because assigning accumulates.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/ServiceRegistrationCallbacks.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceRegistrationCallbacksGuardTests {
  /// <summary>Slots that are not one-per-assembly, with the reason.</summary>
  private static readonly Dictionary<string, string> _notPerAssembly = new(StringComparer.Ordinal) {
    ["HostingIntegration"] = "set only by Whizbang.Hosting.AspNet's own module initializer: one assembly, one callback",
    ["MessageTypeCatalog"] = "accumulates, and its getter registers one union catalog built through a probe; "
      + "ServiceRegistrationCallbacksCatalogUnionTests pins that",
  };

  [Test]
  public async Task EverySlotAGeneratedModuleInitializerSets_KeepsEveryAssemblysCallbackAsync() {
    var slots = typeof(ServiceRegistrationCallbacks).GetProperties(BindingFlags.Public | BindingFlags.Static)
      .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType) && p.CanWrite)
      .ToList();
    var failures = new List<string>();
    foreach (var slot in slots.Where(s => !_notPerAssembly.ContainsKey(s.Name))) {
      var saved = slot.GetValue(null);
      try {
        var ran = new List<string>();
        slot.SetValue(null, null);
        if (slot.PropertyType == typeof(Action<IServiceCollection>)) {
          slot.SetValue(null, (Action<IServiceCollection>)(_ => ran.Add("Orders")));
          slot.SetValue(null, (Action<IServiceCollection>)(_ => ran.Add("Billing")));
          ((Action<IServiceCollection>)slot.GetValue(null)!)(new ServiceCollection());
        } else if (slot.PropertyType == typeof(Action<IServiceCollection, ServiceRegistrationOptions>)) {
          slot.SetValue(null, (Action<IServiceCollection, ServiceRegistrationOptions>)((_, _) => ran.Add("Orders")));
          slot.SetValue(null, (Action<IServiceCollection, ServiceRegistrationOptions>)((_, _) => ran.Add("Billing")));
          ((Action<IServiceCollection, ServiceRegistrationOptions>)slot.GetValue(null)!)(new ServiceCollection(), new ServiceRegistrationOptions());
        } else {
          failures.Add($"{slot.Name}: a slot of a new shape ({slot.PropertyType.Name}); make it accumulate and add the shape here");
          continue;
        }
        if (!ran.SequenceEqual(["Orders", "Billing"])) {
          failures.Add($"{slot.Name}: ran [{string.Join(", ", ran)}], not every assembly's callback in order");
        }
      } finally {
        slot.SetValue(null, null);
        slot.SetValue(null, saved);
      }
    }

    await Assert.That(slots.Count).IsGreaterThanOrEqualTo(7)
      .Because("precondition: the slots are found by reflection, not listed");
    await Assert.That(failures).IsEmpty()
      .Because("a slot that keeps only the last assembly's callback silently drops every other assembly's "
             + "registrations: " + string.Join(" | ", failures));
  }
}
