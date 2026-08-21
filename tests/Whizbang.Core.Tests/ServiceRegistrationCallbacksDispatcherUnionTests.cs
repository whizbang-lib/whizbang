using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests;

/// <summary>
/// Issue #491: every Whizbang-compiled assembly's generated module initializer assigns
/// <see cref="ServiceRegistrationCallbacks.Dispatcher"/>, and a single-valued setter let the last
/// initializer win — one assembly's <c>AddReceptors()</c> silently displacing every other's, so a
/// message dispatched to a receptor in a displaced assembly simply never ran. Same defect class
/// the message-type catalog already fixed (see
/// <see cref="ServiceRegistrationCallbacksCatalogUnionTests"/>): the setter must ACCUMULATE.
/// </summary>
/// <remarks>
/// <c>[NotInParallel]</c>, same group as every other mutator of the process-global
/// <see cref="ServiceRegistrationCallbacks"/> state.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceRegistrationCallbacksDispatcherUnionTests {

  private sealed class HostAssemblyMarker;
  private sealed class ContractsAssemblyMarker;

  [Test]
  public async Task InvokeAll_DispatcherCallbacksFromTwoAssemblies_BothRegistrationsRunAsync() {
    var saved = ServiceRegistrationCallbacks.Dispatcher;
    try {
      ServiceRegistrationCallbacks.Dispatcher = null;   // clear for an isolated two-assembly simulation
      // Two module initializers from two assemblies, each doing exactly what the shipped
      // generated code does — assign its own AddReceptors/AddWhizbangDispatcher callback.
      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<HostAssemblyMarker>();
      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<ContractsAssemblyMarker>();

      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

      await Assert.That(services.Any(d => d.ServiceType == typeof(ContractsAssemblyMarker))).IsTrue();
      await Assert.That(services.Any(d => d.ServiceType == typeof(HostAssemblyMarker))).IsTrue()
        .Because("issue #491: the last module initializer must not displace every other assembly's "
               + "receptors — nothing fails when it does, a handler just silently never runs");
    } finally {
      ServiceRegistrationCallbacks.Dispatcher = null;
      if (saved is not null) {
        ServiceRegistrationCallbacks.Dispatcher = saved;
      }
    }
  }

  [Test]
  public async Task Dispatcher_AssigningNull_ClearsEveryAccumulatedRegistrationAsync() {
    var saved = ServiceRegistrationCallbacks.Dispatcher;
    try {
      ServiceRegistrationCallbacks.Dispatcher = null;
      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<HostAssemblyMarker>();
      ServiceRegistrationCallbacks.Dispatcher = null;   // the test-reset semantics

      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

      await Assert.That(services.Any(d => d.ServiceType == typeof(HostAssemblyMarker))).IsFalse()
        .Because("null keeps its clear-everything meaning so Reset() and test harnesses behave unchanged");
    } finally {
      ServiceRegistrationCallbacks.Dispatcher = null;
      if (saved is not null) {
        ServiceRegistrationCallbacks.Dispatcher = saved;
      }
    }
  }
}
