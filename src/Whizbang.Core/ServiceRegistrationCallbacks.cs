using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Configuration;

namespace Whizbang.Core;

/// <summary>
/// Static registry for service registration callbacks.
/// Consumer assemblies register their discovered services via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// </summary>
/// <remarks>
/// <para>
/// The source generators (ServiceRegistrationGenerator, ReceptorDiscoveryGenerator) generate
/// module initializers that set these callbacks when the consumer assembly loads.
/// <see cref="ServiceCollectionExtensions.AddWhizbang"/> then invokes these callbacks
/// to register all discovered services automatically.
/// </para>
/// <para>
/// This pattern allows generated code in the consumer assembly to be invoked from
/// Whizbang.Core without reflection, while preserving user control via options.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Generated module initializer in consumer assembly:
/// [ModuleInitializer]
/// internal static void RegisterServiceCallbacks() {
///   ServiceRegistrationCallbacks.LensServices = (services, options) =&gt;
///     services.AddLensServices(o =&gt; o.IncludeSelfRegistration = options.IncludeSelfRegistration);
/// }
///
/// // User code - services are auto-registered:
/// services.AddWhizbang();  // Invokes LensServices callback automatically
/// </code>
/// </example>
public static class ServiceRegistrationCallbacks {
  private static readonly Lock _lock = new();

  /// <summary>
  /// Callback for registering discovered lens services (ILensQuery implementations).
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? LensServices { get; set; }

  /// <summary>
  /// Callback for registering discovered perspective services (IPerspective implementations).
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? PerspectiveServices { get; set; }

  /// <summary>
  /// Callback for registering the generated dispatcher and receptor infrastructure.
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection>? Dispatcher { get; set; }

  /// <summary>
  /// Callback for registering the generated <see cref="IPinnedIdRegistry"/>.
  /// Set by the PinnedIdRegistryGenerator's module initializer in the consumer assembly.
  /// </summary>
  public static Action<IServiceCollection>? PinnedIdRegistry { get; set; }

  /// <summary>
  /// Callback for registering the generated <see cref="IMessageTypeCatalog"/>.
  /// Set by the MessageTypeCatalogGenerator's module initializer in EVERY Whizbang-compiled
  /// assembly — the setter therefore ACCUMULATES (each assignment adds a registration) instead of
  /// overwriting, and registration collapses the accumulated per-assembly catalogs into one
  /// <see cref="CompositeMessageTypeCatalog"/> union. A single-valued setter let the last module
  /// initializer win, so a host assembly's catalog displaced its contracts assembly's — leaving
  /// receive-path flag derivation, the ephemeral resolver, the registry populators, the rename
  /// tool, and the fingerprint reconciler blind to every type declared elsewhere (and the
  /// reconciler's full-replace grace sync actively pruning rows for the missing types).
  /// Assigning null clears all registrations (the test-only <see cref="Reset"/> semantics).
  /// </summary>
  public static Action<IServiceCollection>? MessageTypeCatalog {
    get {
      lock (_lock) {
        return _messageTypeCatalogRegistrations.Count == 0 ? null : _registerUnionCatalog;
      }
    }
    set {
      lock (_lock) {
        if (value is null) {
          _messageTypeCatalogRegistrations.Clear();
        } else {
          _messageTypeCatalogRegistrations.Add(value);
        }
      }
    }
  }

  private static readonly List<Action<IServiceCollection>> _messageTypeCatalogRegistrations = [];

  /// <summary>Test-only: snapshot the accumulated catalog registrations for save/restore.</summary>
  internal static IReadOnlyList<Action<IServiceCollection>> SnapshotMessageTypeCatalogRegistrations() {
    lock (_lock) {
      return [.. _messageTypeCatalogRegistrations];
    }
  }

  /// <summary>Test-only: restore a previously snapshotted registration set.</summary>
  internal static void RestoreMessageTypeCatalogRegistrations(IReadOnlyList<Action<IServiceCollection>> snapshot) {
    lock (_lock) {
      _messageTypeCatalogRegistrations.Clear();
      _messageTypeCatalogRegistrations.AddRange(snapshot);
    }
  }

  /// <summary>
  /// Runs every accumulated per-assembly catalog registration against a probe collection, then
  /// registers the union of the resulting catalogs as THE <see cref="IMessageTypeCatalog"/>. The
  /// probe keeps the shipped generated callbacks working unchanged (each does a plain
  /// <c>AddSingleton&lt;IMessageTypeCatalog, GeneratedMessageTypeCatalog&gt;</c>) while the real
  /// container only ever sees the composite.
  /// </summary>
  private static void _registerUnionCatalog(IServiceCollection services) {
    List<Action<IServiceCollection>> snapshot;
    lock (_lock) {
      snapshot = [.. _messageTypeCatalogRegistrations];
    }
    var probe = new ServiceCollection();
    foreach (var register in snapshot) {
      register(probe);
    }
    using var probeProvider = probe.BuildServiceProvider();
    // Generated catalogs are stateless views over compile-time statics, so materializing the
    // union while the probe provider is alive keeps nothing disposable alive afterwards.
    var union = new CompositeMessageTypeCatalog(probeProvider.GetServices<IMessageTypeCatalog>());
    services.AddSingleton<IMessageTypeCatalog>(union);
  }

  /// <summary>
  /// Callback for registering discovered <see cref="Whizbang.Core.Messaging.IRawReceptor"/>
  /// implementations and the <see cref="Whizbang.Core.Messaging.IRawReceptorRegistry"/>.
  /// Set by the RawReceptorDiscoveryGenerator's module initializer in the consumer assembly.
  /// </summary>
  public static Action<IServiceCollection>? RawReceptors { get; set; }

  /// <summary>
  /// Callback for wiring the Path 1 atomic-upsert options provider on
  /// <c>BaseUpsertStrategy.PathOnePersistenceOptionsProvider</c>. Set by the
  /// PerspectivePersistenceJsonContextGenerator's module initializer in the consumer
  /// assembly. Fires inside <see cref="InvokeAll"/> so that ordering is deterministic
  /// regardless of cross-assembly module-load order.
  /// </summary>
  /// <remarks>
  /// When fired, the callback sets <c>BaseUpsertStrategy.PathOnePersistenceOptionsProvider</c>
  /// to a delegate returning <c>PerspectivePersistenceJsonContext.CreateOptions(
  /// MessageJsonContext.Default, InfrastructureJsonContext.Default)</c>. After this point,
  /// every <c>UpsertPerspectiveRowAsync</c> call routes through the atomic
  /// <c>INSERT…ON CONFLICT DO UPDATE</c> path instead of the slice-19 retry loop —
  /// structurally eliminating the 23505 dup-key storm.
  /// </remarks>
  public static Action<IServiceCollection>? PerspectivePersistenceOptions { get; set; }

  /// <summary>
  /// Callback that folds the ASP.NET hosting integration (<c>AddWhizbangAspNet()</c>) into
  /// <see cref="ServiceCollectionExtensions.AddWhizbang"/>. Set by the Whizbang.Hosting.AspNet
  /// assembly's <c>[ModuleInitializer]</c>, so it is present only when that assembly is loaded — i.e.
  /// when the ASP.NET hosting library is referenced/used. Invoked by <c>AddWhizbang</c> unless the
  /// consumer opts out via <c>WhizbangCoreOptions.AutoRegisterAspNetHosting = false</c>.
  /// </summary>
  public static Action<IServiceCollection>? HostingIntegration { get; set; }

  /// <summary>
  /// Invokes all registered service callbacks with the provided options.
  /// Called by <see cref="ServiceCollectionExtensions.AddWhizbang"/> to auto-register services.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="options">Options controlling registration behavior.</param>
  internal static void InvokeAll(IServiceCollection services, ServiceRegistrationOptions options) {
    lock (_lock) {
      LensServices?.Invoke(services, options);
      PerspectiveServices?.Invoke(services, options);
      Dispatcher?.Invoke(services);
      RawReceptors?.Invoke(services);
      PinnedIdRegistry?.Invoke(services);
      MessageTypeCatalog?.Invoke(services);
      if (MessageTypeCatalog is not null) {
        // The ephemeral-mode resolver derives its ClrTypeName -> EphemeralInfo lookup from the
        // catalog just registered above, so it is only wired when a catalog exists to feed it.
        services.TryAddSingleton<IEphemeralModeResolver, EphemeralModeResolver>();
        services.TryAddSingleton<IEventMarkerResolver, EventMarkerResolver>();
      }
      // Path 1 atomic-upsert wiring fires last — it depends on JsonSerializerContext
      // statics that are populated by their own [ModuleInitializer] runs on consumer
      // assembly load, and on BaseUpsertStrategy's static hook which is process-wide.
      // It doesn't add services, so the IServiceCollection argument is unused.
      PerspectivePersistenceOptions?.Invoke(services);
    }
  }

  /// <summary>
  /// Resets all callbacks. Used for testing purposes only.
  /// </summary>
  internal static void Reset() {
    lock (_lock) {
      LensServices = null;
      PerspectiveServices = null;
      RawReceptors = null;
      Dispatcher = null;
      PinnedIdRegistry = null;
      MessageTypeCatalog = null;
      PerspectivePersistenceOptions = null;
      HostingIntegration = null;
    }
  }
}
