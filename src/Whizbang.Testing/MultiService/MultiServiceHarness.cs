using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.MultiService;

/// <summary>
/// A multi-service integration harness: N independent service hosts — each with its OWN generated
/// catalogs, its OWN container built through the PUBLIC <c>AddWhizbang</c> chain, and its OWN
/// receive worker — joined by a shared <see cref="InMemoryWireTransport"/> whose boundary is real
/// JSON bytes. Built for the class of defect that single-service tests structurally cannot see:
/// behavior that only appears when a message LEAVES the process that produced it (flag/TTL
/// derivation from wire type names, catalog-union coverage, envelope-type resolution, echo
/// discard). Each service's catalog assignments run through
/// <see cref="ServiceRegistrationCallbacks"/> exactly as generated module initializers do; the
/// harness snapshots and restores the process-global callback state around every build, so
/// services stay isolated from each other and from the surrounding test process.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
public sealed class MultiServiceHarness : IAsyncDisposable {
  private readonly List<ServiceRuntime> _services;
  private readonly JsonSerializerOptions _wireOptions;

  /// <summary>The shared wire connecting every service in the harness.</summary>
  public InMemoryWireTransport Wire { get; }

  private MultiServiceHarness(InMemoryWireTransport wire, List<ServiceRuntime> services, JsonSerializerOptions wireOptions) {
    Wire = wire;
    _services = services;
    _wireOptions = wireOptions;
  }

  /// <summary>Starts building a harness. Add services, then <see cref="Builder.StartAsync"/>.</summary>
  public static Builder Create() => new();

  /// <summary>The named service's runtime (container, worker, captured storage).</summary>
  public ServiceRuntime GetService(string name) =>
    _services.FirstOrDefault(s => s.Name == name)
      ?? throw new ArgumentException($"No service named '{name}' in the harness.", nameof(name));

  /// <summary>
  /// Publishes a typed message onto the shared wire AS a given service would: a typed envelope,
  /// serialized to bytes at the boundary, carrying the standard wire envelope-type string. The
  /// payload type must be registered in a <c>JsonSerializerContext</c> visible to
  /// <see cref="JsonContextRegistry"/> (the same requirement production has).
  /// </summary>
  public async Task PublishAsync<TMessage>(TMessage payload, string topic = MultiServiceHarnessDefaults.SHARED_TOPIC)
      where TMessage : notnull {
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeType =
      $"Whizbang.Core.Messaging.MessageEnvelope`1[[{typeof(TMessage).AssemblyQualifiedName}]], Whizbang.Core";
    await Wire.PublishAsync(envelope, new TransportDestination(topic), envelopeType);
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    foreach (var service in _services) {
      await service.Worker.StopAsync(CancellationToken.None);
      await service.Provider.DisposeAsync();
    }
  }

  /// <summary>One running service inside the harness.</summary>
  public sealed class ServiceRuntime {
    internal ServiceRuntime(string name, ServiceProvider provider, TransportConsumerWorker worker, CapturingWorkCoordinator inbox) {
      Name = name;
      Provider = provider;
      Worker = worker;
      Inbox = inbox;
    }

    /// <summary>The service's harness name.</summary>
    public string Name { get; }

    /// <summary>The service's own container, built through the public AddWhizbang chain.</summary>
    public ServiceProvider Provider { get; }

    /// <summary>The service's receive worker (already started).</summary>
    public TransportConsumerWorker Worker { get; }

    /// <summary>The service's captured inbox storage.</summary>
    public CapturingWorkCoordinator Inbox { get; }
  }

  /// <summary>Per-service configuration collected by the builder.</summary>
  public sealed class ServiceDefinition {
    internal ServiceDefinition(string name) {
      Name = name;
    }

    internal string Name { get; }
    internal List<Action<IServiceCollection>> CatalogRegistrations { get; } = [];
    internal Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// Adds a catalog exactly as a generated module initializer would — the registration flows
    /// through <see cref="ServiceRegistrationCallbacks.MessageTypeCatalog"/>, so the service's
    /// catalog union is built by the same production path.
    /// </summary>
    public ServiceDefinition WithCatalog<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TCatalog>() where TCatalog : class, IMessageTypeCatalog {
      CatalogRegistrations.Add(static services => services.AddSingleton<IMessageTypeCatalog, TCatalog>());
      return this;
    }

    /// <summary>Extra registrations applied before the AddWhizbang chain (overrides harness defaults).</summary>
    public ServiceDefinition Configure(Action<IServiceCollection> configure) {
      ConfigureServices = configure;
      return this;
    }
  }

  /// <summary>Builds and starts a <see cref="MultiServiceHarness"/>.</summary>
  public sealed class Builder {
    private readonly List<ServiceDefinition> _definitions = [];

    internal Builder() {
    }

    /// <summary>Adds a named service to the harness.</summary>
    public Builder AddService(string name, Action<ServiceDefinition> configure) {
      ArgumentException.ThrowIfNullOrWhiteSpace(name);
      ArgumentNullException.ThrowIfNull(configure);
      var definition = new ServiceDefinition(name);
      configure(definition);
      _definitions.Add(definition);
      return this;
    }

    /// <summary>
    /// Builds every service container (sequentially — the module-initializer callback state is
    /// process-global, snapshotted and restored around each build), starts every receive worker,
    /// and returns the running harness.
    /// </summary>
    public async Task<MultiServiceHarness> StartAsync() {
      if (_definitions.Count == 0) {
        throw new InvalidOperationException("Add at least one service before starting the harness.");
      }

      var wireOptions = JsonContextRegistry.CreateCombinedOptions();
      var wire = new InMemoryWireTransport(wireOptions);
      var runtimes = new List<ServiceRuntime>();

      foreach (var definition in _definitions) {
        var saved = ServiceRegistrationCallbacks.SnapshotMessageTypeCatalogRegistrations();
        ServiceProvider? provider = null;
        try {
          ServiceRegistrationCallbacks.MessageTypeCatalog = null;
          foreach (var catalogRegistration in definition.CatalogRegistrations) {
            ServiceRegistrationCallbacks.MessageTypeCatalog = catalogRegistration;
          }

          var inbox = new CapturingWorkCoordinator();
          var services = new ServiceCollection();
          // Host parity: a real host's builder supplies logging; the transport package supplies
          // JsonSerializerOptions; the storage driver supplies the coordinator; the generated
          // registry claims consumers. The harness supplies equivalents so the PUBLIC chain below
          // is the only framework entry point involved.
          services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
          services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
          services.AddSingleton(wireOptions);
          services.AddSingleton<ITransport>(wire);
          services.AddScoped<IWorkCoordinator>(_ => inbox);
          services.AddSingleton<IReceptorRegistryQuery>(new ClaimAllReceptorRegistryQuery());
          definition.ConfigureServices?.Invoke(services);

          services.AddWhizbang()
            .WithRouting(r => {
              r.Inbox.UseSharedTopic(MultiServiceHarnessDefaults.SHARED_TOPIC);
              r.Outbox.UseSharedTopic(MultiServiceHarnessDefaults.SHARED_TOPIC);
            })
            .AddTransportConsumer();

          provider = services.BuildServiceProvider();
          var worker = ActivatorUtilities.CreateInstance<TransportConsumerWorker>(provider);
          await worker.StartAsync(CancellationToken.None);
          await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
          runtimes.Add(new ServiceRuntime(definition.Name, provider, worker, inbox));
          provider = null;
        } finally {
          if (provider is not null) {
            await provider.DisposeAsync();
          }
          ServiceRegistrationCallbacks.RestoreMessageTypeCatalogRegistrations(saved);
        }
      }

      return new MultiServiceHarness(wire, runtimes, wireOptions);
    }
  }

  private sealed class ClaimAllReceptorRegistryQuery : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }
}

/// <summary>Shared defaults for the multi-service harness.</summary>
public static class MultiServiceHarnessDefaults {
  /// <summary>The shared inbox/outbox topic every harness service subscribes to.</summary>
  public const string SHARED_TOPIC = "inbox";
}
