using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests;

/// <summary>
/// The message-type catalog must be the UNION of every loaded assembly's generated catalog.
/// Each assembly's generated module initializer assigns
/// <see cref="ServiceRegistrationCallbacks.MessageTypeCatalog"/>; historically the property was
/// single-valued, so the last initializer to run displaced every other assembly's catalog — a host
/// assembly's catalog shadowing the contracts assembly's, leaving the receive-path flag derivation,
/// the ephemeral resolver, the registry populators, the rename tool, and the fingerprint reconciler
/// blind to every type not declared in the winning assembly.
/// </summary>
public class ServiceRegistrationCallbacksCatalogUnionTests {

  private sealed class HostEvent;
  private sealed class ContractsEvent;

  private sealed class HostAssemblyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(HostEvent), TypeNameFormatter.FormatClrTypeName(typeof(HostEvent)), "event", null),
    ];
  }

  private sealed class ContractsAssemblyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(ContractsEvent), TypeNameFormatter.FormatClrTypeName(typeof(ContractsEvent)), "event", null) { IsCollective = true },
    ];
  }

  [Test]
  public async Task InvokeAll_CatalogCallbacksFromTwoAssemblies_RegistersTheUnionAsync() {
    var saved = ServiceRegistrationCallbacks.SnapshotMessageTypeCatalogRegistrations();
    try {
      ServiceRegistrationCallbacks.MessageTypeCatalog = null;   // clear for an isolated two-assembly simulation
      // Two module initializers from two assemblies, each doing exactly what the shipped
      // generated code does — assign its own AddSingleton callback.
      ServiceRegistrationCallbacks.MessageTypeCatalog = static services =>
        services.AddSingleton<IMessageTypeCatalog, HostAssemblyCatalog>();
      ServiceRegistrationCallbacks.MessageTypeCatalog = static services =>
        services.AddSingleton<IMessageTypeCatalog, ContractsAssemblyCatalog>();

      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
      await using var sp = services.BuildServiceProvider();
      var catalog = sp.GetRequiredService<IMessageTypeCatalog>();
      var names = catalog.GetAll().Select(e => e.ClrTypeName).ToList();

      await Assert.That(names).Contains(TypeNameFormatter.FormatClrTypeName(typeof(ContractsEvent)));
      await Assert.That(names).Contains(TypeNameFormatter.FormatClrTypeName(typeof(HostEvent)))
        .Because("a later assembly's catalog registration must not displace an earlier one — the " +
                 "receive-path flag derivation resolves contracts event types through this catalog");
    } finally {
      ServiceRegistrationCallbacks.RestoreMessageTypeCatalogRegistrations(saved);
    }
  }

  [Test]
  public async Task InvokeAll_CatalogRegisteredAfterInvokeAll_IsStillInTheUnionAsync() {
    // [ModuleInitializer]s run on first assembly TOUCH, which can be after AddWhizbang has already
    // run InvokeAll — an eagerly-materialized union would freeze that assembly out forever. The
    // union must snapshot at first RESOLUTION, so a late initializer's catalog still lands.
    var saved = ServiceRegistrationCallbacks.SnapshotMessageTypeCatalogRegistrations();
    try {
      ServiceRegistrationCallbacks.MessageTypeCatalog = null;
      ServiceRegistrationCallbacks.MessageTypeCatalog = static services =>
        services.AddSingleton<IMessageTypeCatalog, HostAssemblyCatalog>();

      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

      // A lazily-loaded assembly's module initializer fires AFTER InvokeAll:
      ServiceRegistrationCallbacks.MessageTypeCatalog = static services =>
        services.AddSingleton<IMessageTypeCatalog, ContractsAssemblyCatalog>();

      await using var sp = services.BuildServiceProvider();
      var names = sp.GetRequiredService<IMessageTypeCatalog>().GetAll().Select(e => e.ClrTypeName).ToList();

      await Assert.That(names).Contains(TypeNameFormatter.FormatClrTypeName(typeof(ContractsEvent)))
        .Because("a catalog registered by a module initializer that runs after AddWhizbang must " +
                 "still be part of the union — assembly-load order is not a correctness input.");
      await Assert.That(names).Contains(TypeNameFormatter.FormatClrTypeName(typeof(HostEvent)));
    } finally {
      ServiceRegistrationCallbacks.RestoreMessageTypeCatalogRegistrations(saved);
    }
  }
}
