// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.TraceStoreOptIn;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Component.Tests.TraceStoreOptIn {
  public record TraceOptInCommand(Guid Id) : ICommand;

  public sealed class TraceOptInCommandReceptor : IReceptor<TraceOptInCommand> {
    public ValueTask HandleAsync(TraceOptInCommand message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }
}

namespace Whizbang.Core.Tests.Dispatcher {
  /// <summary>
  /// A default host does no trace work on dispatch (#1601): no store is registered, so the
  /// generated dispatcher has none, and a plain void receptor runs without an envelope ever being
  /// built for it. A host that opts in gets every dispatch recorded in a bounded store.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Observability/TraceStoreRegistration.cs</code-under-test>
  /// <docs>fundamentals/persistence/observability#trace-store-registration</docs>
  public class TraceStoreOptInComponentTests {
    [Test]
    public async Task LocalInvoke_DefaultHost_BuildsNoTraceEnvelopeAsync() {
      var registry = new CountingEnvelopeRegistry();
      await using var provider = _buildProvider(registry, optIn: false);

      await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)new TraceOptInCommand(Guid.CreateVersion7()), new DispatchOptions());

      await Assert.That(provider.GetService<ITraceStore>()).IsNull();
      await Assert.That(registry.Registered).IsEqualTo(0)
        .Because("with no trace store and nothing after the receptor, the dispatcher builds no envelope");
    }

    [Test]
    public async Task LocalInvoke_OptedInHost_RecordsTheDispatchAsync() {
      var registry = new CountingEnvelopeRegistry();
      await using var provider = _buildProvider(registry, optIn: true);

      await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)new TraceOptInCommand(Guid.CreateVersion7()), new DispatchOptions());

      var store = (InMemoryTraceStore)provider.GetRequiredService<ITraceStore>();
      await Assert.That(store.Count).IsEqualTo(1);
      await Assert.That(registry.Registered).IsEqualTo(1);
    }

    private static ServiceProvider _buildProvider(CountingEnvelopeRegistry registry, bool optIn) {
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddLogging();
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
      services.AddWhizbang();
      if (optIn) {
        services.AddWhizbangInMemoryTraceStore();
      }
      services.AddSingleton<IEnvelopeRegistry>(registry);
      services.AddReceptors();
      services.AddWhizbangDispatcher();
      return services.BuildServiceProvider();
    }

    /// <summary>Counts envelope registrations, the dispatcher's sign that it built an envelope.</summary>
    private sealed class CountingEnvelopeRegistry : IEnvelopeRegistry {
      private int _registered;

      public int Registered => Volatile.Read(ref _registered);

      public void Register<T>(MessageEnvelope<T> envelope) => Interlocked.Increment(ref _registered);

      public MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull => null;

      public void Unregister<T>(T message) where T : notnull {
        // Nothing is held, so nothing to release
      }

      public void Unregister<T>(MessageEnvelope<T> envelope) {
        // Nothing is held, so nothing to release
      }
    }
  }
}
