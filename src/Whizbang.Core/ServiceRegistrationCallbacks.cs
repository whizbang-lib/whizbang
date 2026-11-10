// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// Set by the source-generated module initializer in EVERY assembly that declares lenses, so the setter
  /// ACCUMULATES (each assignment adds a registration) and the getter returns one delegate that runs them all, in
  /// the order set. A single-valued slot let the next assembly's initializer overwrite the last, and the lenses of
  /// every assembly but one were never registered (#2199). Assigning null clears all registrations.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/ServiceRegistrationCallbacksAccumulateTests.cs:LensServices_RunsEveryAssemblysCallback_InTheOrderSetAsync</tests>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? LensServices {
    get => _lensServices.Combined;
    set => _lensServices.Set(value);
  }

  /// <summary>
  /// Callback for registering discovered perspective services (IPerspective implementations). Accumulates, as
  /// <see cref="LensServices"/> does, so every assembly's perspective services are registered (#2199).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/ServiceRegistrationCallbacksAccumulateTests.cs:PerspectiveServices_RunsEveryAssemblysCallback_InTheOrderSetAsync</tests>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? PerspectiveServices {
    get => _perspectiveServices.Combined;
    set => _perspectiveServices.Set(value);
  }

  private static readonly AccumulatedCallbacks<Action<IServiceCollection, ServiceRegistrationOptions>> _lensServices = new(_lock);
  private static readonly AccumulatedCallbacks<Action<IServiceCollection, ServiceRegistrationOptions>> _perspectiveServices = new(_lock);
  private static readonly AccumulatedCallbacks<Action<IServiceCollection>> _pinnedIdRegistry = new(_lock);
  private static readonly AccumulatedCallbacks<Action<IServiceCollection>> _rawReceptors = new(_lock);

  /// <summary>
  /// One startup slot every assembly sets: each assignment adds a callback, null clears them, and the combined
  /// delegate runs every callback in the order set. Built from a snapshot, so a saved value replays exactly the set
  /// it was read from. Each callback keeps the name of the assembly that registered it (none when it was assigned
  /// through the property), so a host replays only its own service's (#2243).
  /// </summary>
  private sealed class AccumulatedCallbacks<TCallback>(Lock gate) where TCallback : Delegate {
    private readonly List<(string? Assembly, TCallback Callback)> _entries = [];

    public TCallback? Combined {
      get {
        lock (gate) {
          return (TCallback?)Delegate.Combine([.. _entries.Select(e => e.Callback)]);
        }
      }
    }

    public bool Any {
      get {
        lock (gate) {
          return _entries.Count > 0;
        }
      }
    }

    public void Set(TCallback? value) {
      lock (gate) {
        if (value is null) {
          _entries.Clear();
        } else {
          _entries.Add((null, value));
        }
      }
    }

    public void Add(string assembly, TCallback callback) {
      ArgumentNullException.ThrowIfNull(assembly);
      ArgumentNullException.ThrowIfNull(callback);
      lock (gate) {
        _entries.Add((assembly, callback));
      }
    }

    public (string? Assembly, TCallback Callback)[] Entries {
      get {
        lock (gate) {
          return [.. _entries];
        }
      }
    }

    public void Restore((string? Assembly, TCallback Callback)[] entries) {
      lock (gate) {
        _entries.Clear();
        _entries.AddRange(entries);
      }
    }
  }

  /// <summary>
  /// Callback for registering the generated dispatcher and receptor infrastructure.
  /// Set by the source-generated module initializer in EVERY Whizbang-compiled assembly that
  /// declares receptors — the setter therefore ACCUMULATES (each assignment adds a registration)
  /// instead of overwriting, exactly as <see cref="MessageTypeCatalog"/> does. A single-valued
  /// setter let the last module initializer win, so one assembly's <c>AddReceptors()</c> silently
  /// displaced every other's: a message dispatched to a receptor in a displaced assembly simply
  /// never ran, with no exception, no log line, and no startup diagnostic (issue #491).
  /// Assigning null clears all registrations (the test-only <see cref="Reset"/> semantics).
  /// </summary>
  public static Action<IServiceCollection>? Dispatcher {
    // The combined delegate is built from a snapshot, never a reference to the live list: harnesses save this
    // property, clear, and later re-assign the saved value, and a delegate that read the live list would then
    // contain itself and recurse without bound (observed as a stack overflow).
    get => _dispatcher.Combined;
    set => _dispatcher.Set(value);
  }

  private static readonly AccumulatedCallbacks<Action<IServiceCollection>> _dispatcher = new(_lock);

  /// <summary>Test-only: snapshot the accumulated dispatcher registrations for save/restore.</summary>
  internal static IReadOnlyList<Action<IServiceCollection>> SnapshotDispatcherRegistrations() =>
    [.. _dispatcher.Entries.Select(e => e.Callback)];

  /// <summary>Test-only: restore a previously snapshotted dispatcher registration set.</summary>
  internal static void RestoreDispatcherRegistrations(IReadOnlyList<Action<IServiceCollection>> snapshot) =>
    _dispatcher.Restore([.. snapshot.Select(callback => ((string?)null, callback))]);



  /// <summary>
  /// Callback for registering the generated <see cref="IPinnedIdRegistry"/>. Set by the PinnedIdRegistryGenerator's
  /// module initializer in every assembly that pins types (Whizbang.Core among them), so it accumulates, as
  /// <see cref="LensServices"/> does; each callback contributes its registry to the one the host serves (#2198).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/ServiceRegistrationCallbacksAccumulateTests.cs:PinnedIdRegistry_RunsEveryAssemblysCallback_InTheOrderSetAsync</tests>
  public static Action<IServiceCollection>? PinnedIdRegistry {
    get => _pinnedIdRegistry.Combined;
    set => _pinnedIdRegistry.Set(value);
  }

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
    get => _messageTypeCatalog.Any ? _registerUnionCatalog : null;
    set => _messageTypeCatalog.Set(value);
  }

  private static readonly AccumulatedCallbacks<Action<IServiceCollection>> _messageTypeCatalog = new(_lock);

  /// <summary>Test-only: snapshot the accumulated catalog registrations for save/restore.</summary>
  internal static IReadOnlyList<Action<IServiceCollection>> SnapshotMessageTypeCatalogRegistrations() =>
    [.. _messageTypeCatalog.Entries.Select(e => e.Callback)];

  /// <summary>Test-only: restore a previously snapshotted registration set.</summary>
  internal static void RestoreMessageTypeCatalogRegistrations(IReadOnlyList<Action<IServiceCollection>> snapshot) =>
    _messageTypeCatalog.Restore([.. snapshot.Select(callback => ((string?)null, callback))]);

  /// <summary>
  /// Runs every accumulated per-assembly catalog registration against a probe collection, then
  /// registers the union of the resulting catalogs as THE <see cref="IMessageTypeCatalog"/>. The
  /// probe keeps the shipped generated callbacks working unchanged (each does a plain
  /// <c>AddSingleton&lt;IMessageTypeCatalog, GeneratedMessageTypeCatalog&gt;</c>) while the real
  /// container only ever sees the composite.
  /// </summary>
  private static void _registerUnionCatalog(IServiceCollection services) {
    // Registered as a FACTORY so the accumulated registrations are snapshotted at FIRST RESOLUTION,
    // not at AddWhizbang time: [ModuleInitializer]s run on first assembly touch, so an assembly the
    // host has not exercised before AddWhizbang would otherwise be silently absent from the union
    // forever. First resolution happens at host start (workers / registry populator), strictly
    // after registration — every initializer that has run by then is included.
    // Only the host's own service's catalogs join the union (#2243); the scope is the host's, resolved here for the
    // same reason the snapshot is taken here.
    services.AddSingleton<IMessageTypeCatalog>(sp => {
      var scope = sp.GetService<ServiceAssemblyScope>() ?? ServiceAssemblyScope.Unscoped;
      var probe = new ServiceCollection();
      foreach (var (_, register) in _messageTypeCatalog.Entries.Where(e => scope.Includes(e.Assembly))) {
        register(probe);
      }
      using var probeProvider = probe.BuildServiceProvider();
      // Generated catalogs are stateless views over compile-time statics, so materializing the
      // union while the probe provider is alive keeps nothing disposable alive afterwards.
      return new CompositeMessageTypeCatalog(probeProvider.GetServices<IMessageTypeCatalog>());
    });
  }

  /// <summary>
  /// Callback for registering discovered <see cref="Whizbang.Core.Messaging.IRawReceptor"/>
  /// implementations and the <see cref="Whizbang.Core.Messaging.IRawReceptorRegistry"/>.
  /// Set by the RawReceptorDiscoveryGenerator's module initializer in every assembly that declares raw receptors,
  /// so it accumulates, as <see cref="LensServices"/> does: a single-valued slot let the last assembly's initializer
  /// overwrite the others', and their raw receptors were never registered (#2198).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/ServiceRegistrationCallbacksAccumulateTests.cs:RawReceptors_RunsEveryAssemblysCallback_InTheOrderSetAsync</tests>
  public static Action<IServiceCollection>? RawReceptors {
    get => _rawReceptors.Combined;
    set => _rawReceptors.Set(value);
  }

  /// <summary>
  /// Callback that folds the ASP.NET hosting integration (<c>AddWhizbangAspNet()</c>) into
  /// <see cref="ServiceCollectionExtensions.AddWhizbang"/>. Set by the Whizbang.Hosting.AspNet
  /// assembly's <c>[ModuleInitializer]</c>, so it is present only when that assembly is loaded — i.e.
  /// when the ASP.NET hosting library is referenced/used. Invoked by <c>AddWhizbang</c> unless the
  /// consumer opts out via <c>WhizbangCoreOptions.AutoRegisterAspNetHosting = false</c>.
  /// </summary>
  public static Action<IServiceCollection>? HostingIntegration { get; set; }

  /// <summary>Registers <paramref name="assembly"/>'s lens services. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceScopedRegistrationTests.cs:EverySlot_ReplaysOnlyTheHostsOwnServiceAsync</tests>
  public static void RegisterLensServices(string assembly, Action<IServiceCollection, ServiceRegistrationOptions> register) =>
    _lensServices.Add(assembly, register);

  /// <summary>Registers <paramref name="assembly"/>'s perspective services. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  public static void RegisterPerspectiveServices(string assembly, Action<IServiceCollection, ServiceRegistrationOptions> register) =>
    _perspectiveServices.Add(assembly, register);

  /// <summary>Registers <paramref name="assembly"/>'s dispatcher and receptors. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  public static void RegisterDispatcher(string assembly, Action<IServiceCollection> register) =>
    _dispatcher.Add(assembly, register);

  /// <summary>Registers <paramref name="assembly"/>'s raw receptors. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  public static void RegisterRawReceptors(string assembly, Action<IServiceCollection> register) =>
    _rawReceptors.Add(assembly, register);

  /// <summary>Registers <paramref name="assembly"/>'s pinned-id registry. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  public static void RegisterPinnedIdRegistry(string assembly, Action<IServiceCollection> register) =>
    _pinnedIdRegistry.Add(assembly, register);

  /// <summary>Registers <paramref name="assembly"/>'s message-type catalog. Called from generated code (#2243).</summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="register">The registration.</param>
  public static void RegisterMessageTypeCatalog(string assembly, Action<IServiceCollection> register) =>
    _messageTypeCatalog.Add(assembly, register);

  /// <summary>
  /// Replays every registered callback of the host's own service into <paramref name="services"/>: called by
  /// <see cref="ServiceCollectionExtensions.AddWhizbang"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Only callbacks whose assembly the host's <see cref="ServiceAssemblyScope"/> includes are replayed (#2243). The
  /// host's anchors may arrive after <c>AddWhizbang()</c> (<c>WithEFCore&lt;TDbContext&gt;()</c> follows it), so the
  /// replay is recorded in the collection and every new anchor replays it again (<see cref="Rescope"/>): the block of
  /// descriptors the callbacks added is replaced by the block the included callbacks add when run against the
  /// collection as it stood before the first replay. The result is what replaying only the host's own callbacks in
  /// the first place would have produced, with the registrations made before and after it left where they are.
  /// </para>
  /// <para>
  /// A second <c>AddWhizbang()</c> call replays again the same way instead of adding every callback's descriptors a
  /// second time, and picks up the callbacks of assemblies loaded since.
  /// </para>
  /// </remarks>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="options">Options controlling registration behavior.</param>
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceScopedRegistrationTests.cs</tests>
  internal static void InvokeAll(IServiceCollection services, ServiceRegistrationOptions options) {
    lock (_lock) {
      var replay = _replayOf(services);
      if (replay is null) {
        replay = new ServiceRegistrationReplay([.. services], options);
        services.Add(replay.Marker);
      }
      _replay(services, replay);
    }
  }

  /// <summary>
  /// Replays the host's callbacks again under its current scope, when <see cref="InvokeAll"/> has already run for
  /// <paramref name="services"/>; called when an anchor is added. Does nothing when the replayed set is unchanged.
  /// </summary>
  /// <param name="services">The host's service collection.</param>
  internal static void Rescope(IServiceCollection services) {
    lock (_lock) {
      var replay = _replayOf(services);
      if (replay is not null) {
        _replay(services, replay);
      }
    }
  }

  private static ServiceRegistrationReplay? _replayOf(IServiceCollection services) =>
    services
      .Where(d => d.ServiceType == typeof(ServiceRegistrationReplay))
      .Select(d => d.ImplementationInstance)
      .OfType<ServiceRegistrationReplay>()
      .FirstOrDefault();

  private static void _replay(IServiceCollection services, ServiceRegistrationReplay replay) {
    var scope = ServiceAssemblyScope.For(services);
    var steps = _steps(scope, replay.Options);
    if (replay.Steps?.Select(s => s.Key).SequenceEqual(steps.Select(s => s.Key)) == true) {
      return;
    }
    var scratch = new ServiceCollection();
    foreach (var descriptor in replay.Before) {
      scratch.Add(descriptor);
    }
    foreach (var (_, run) in steps) {
      run(scratch);
    }
    ServiceDescriptor[] fresh = [.. scratch.Skip(replay.Before.Length)];
    foreach (var descriptor in replay.Added) {
      services.Remove(descriptor);
    }
    var at = services.IndexOf(replay.Marker) + 1;
    for (var i = 0; i < fresh.Length; i++) {
      services.Insert(at + i, fresh[i]);
    }
    replay.Added = fresh;
    replay.Steps = steps;
  }

  /// <summary>Every included callback, in the order the slots have always been replayed.</summary>
  private static List<(Delegate Key, Action<IServiceCollection> Run)> _steps(ServiceAssemblyScope scope, ServiceRegistrationOptions options) {
    var steps = new List<(Delegate Key, Action<IServiceCollection> Run)>();
    foreach (var (_, callback) in _lensServices.Entries.Where(e => scope.Includes(e.Assembly))) {
      steps.Add((callback, services => callback(services, options)));
    }
    foreach (var (_, callback) in _perspectiveServices.Entries.Where(e => scope.Includes(e.Assembly))) {
      steps.Add((callback, services => callback(services, options)));
    }
    foreach (var slot in (AccumulatedCallbacks<Action<IServiceCollection>>[])[_dispatcher, _rawReceptors, _pinnedIdRegistry]) {
      foreach (var (_, callback) in slot.Entries.Where(e => scope.Includes(e.Assembly))) {
        steps.Add((callback, callback));
      }
    }
    if (_messageTypeCatalog.Any) {
      // One union catalog whatever the assemblies; it picks the host's own when first resolved.
      steps.Add((_registerUnionCatalog, _registerUnionCatalog));
      steps.Add((_registerCatalogResolvers, _registerCatalogResolvers));
    }
    return steps;
  }

  private static readonly Action<IServiceCollection> _registerCatalogResolvers = static services => {
    // The ephemeral-mode resolver derives its ClrTypeName -> EphemeralInfo lookup from the catalog, so it is only
    // wired when a catalog exists to feed it.
    services.TryAddSingleton<IEphemeralModeResolver, EphemeralModeResolver>();
    services.TryAddSingleton<IEventMarkerResolver, EventMarkerResolver>();
    services.TryAddSingleton<SystemEvents.AuditTypeResolver>();
  };

  /// <summary>One host's record of its replay: what stood before it, and what it added.</summary>
  private sealed class ServiceRegistrationReplay {
    public ServiceRegistrationReplay(ServiceDescriptor[] before, ServiceRegistrationOptions options) {
      Before = before;
      Options = options;
      Marker = ServiceDescriptor.Singleton(this);
    }

    public ServiceDescriptor[] Before { get; }
    public ServiceRegistrationOptions Options { get; }
    public ServiceDescriptor Marker { get; }
    public ServiceDescriptor[] Added { get; set; } = [];
    public List<(Delegate Key, Action<IServiceCollection> Run)>? Steps { get; set; }
  }

  /// <summary>
  /// Test-only: clears every slot, keeping each callback's assembly, and returns a handle that puts them back.
  /// </summary>
  internal static IDisposable ReplaceForTesting() {
    lock (_lock) {
      var lens = _lensServices.Entries;
      var perspective = _perspectiveServices.Entries;
      var dispatcher = _dispatcher.Entries;
      var raw = _rawReceptors.Entries;
      var pinned = _pinnedIdRegistry.Entries;
      var catalog = _messageTypeCatalog.Entries;
      Reset();
      return new SlotRestore(() => {
        _lensServices.Restore(lens);
        _perspectiveServices.Restore(perspective);
        _dispatcher.Restore(dispatcher);
        _rawReceptors.Restore(raw);
        _pinnedIdRegistry.Restore(pinned);
        _messageTypeCatalog.Restore(catalog);
      });
    }
  }

  private sealed class SlotRestore(Action restore) : IDisposable {
    public void Dispose() {
      lock (_lock) {
        restore();
      }
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
      HostingIntegration = null;
    }
  }
}
