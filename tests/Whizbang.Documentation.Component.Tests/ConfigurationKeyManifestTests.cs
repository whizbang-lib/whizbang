// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Archive.AzureBlob;
using Whizbang.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Health;
using Whizbang.Core.Offloads;
using Whizbang.Core.Routing;
using Whizbang.Core.RunControl;
using Whizbang.Core.Security;
using Whizbang.Core.Signals;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Hosting.AspNet;
using Whizbang.Offloads.AzureBlob;
using Whizbang.Sagas;
using Whizbang.Transports.AzureServiceBus;
using Whizbang.Transports.HotChocolate;
using Whizbang.Transports.HotChocolate.Middleware;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Documentation.Tests;

/// <summary>
/// Locks the configuration keys the library reads to <c>tests/Whizbang.Documentation.Tests/Baselines/configuration-keys.txt</c>, the list the
/// documentation site checks every key it documents against.
/// </summary>
/// <remarks>
/// <para>
/// The keys are recorded, not listed by hand: each package is registered the way a host does, every
/// options type it configures is resolved, and a configuration source that holds no values records
/// every key it is asked for. A binder added, removed or renamed changes the recording, and this test
/// fails until the manifest (and so the documentation) is updated with it.
/// </para>
/// <para>
/// Regenerate the manifest with <c>WHIZBANG_UPDATE_CONFIGURATION_KEYS=1</c>; the diff is the review.
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference</docs>
[NotInParallel(nameof(ConfigurationKeyManifestTests))]
public class ConfigurationKeyManifestTests {
  private const string FAKE_SERVICE_BUS =
    "Endpoint=sb://manifest.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=bWFuaWZlc3Q=";

  internal static Task<IReadOnlyCollection<string>> RecordKeysTheLibraryReadsAsync() => _composeLibraryAsync(null, null);

  /// <summary>Each options instance the composition resolves, with the keys its resolution read first.</summary>
  private static Action<object, IReadOnlyCollection<string>>? _resolvedOptions;

  /// <summary>
  /// Composes every package the way a host does over a configuration that records what it is asked for, resolves every
  /// options type it configures, and returns the keys read.
  /// </summary>
  /// <param name="values">Values to answer with, beside the gate seeds; null for none.</param>
  /// <param name="inspect">Runs against the composed services before the provider is disposed; null for nothing.</param>
  private static async Task<IReadOnlyCollection<string>> _composeLibraryAsync(
      IReadOnlyDictionary<string, string>? values, Action<IServiceProvider, IServiceCollection>? inspect) {
    // Keys whose value decides whether more keys are read. Each seed opens one gate.
    var seeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      // A named cipher makes the cipher's key settings required, so they are read.
      ["Whizbang:BodyOffload:CipherName"] = "aes-gcm",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "manifest-key",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(new byte[32]),
    };
    foreach (var (key, value) in values ?? new Dictionary<string, string>()) {
      seeds[key] = value;
    }
    var recorder = new ConfigurationKeyRecorder(seeds);
    var configuration = new ConfigurationBuilder().Add(recorder).Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();

    services.AddWhizbang()
      .WithRouting(_ => { })
      .AddTransportConsumer();
    services.AddWhizbangWorkers();
    services.AddWhizbangSignalBus();
    services.AddWhizbangBodyOffload();
    services.AddWhizbangBodyCipherFromConfiguration(configuration);
    services.AddWhizbangPostgresNotifications();
    // A database named by the placeholder records its section as a consumer-chosen name.
    services.AddWhizbangPostgresOptionsBinding(ConfigurationKeyRecorder.PLACEHOLDER);
    // Every capability provider (both Postgres drivers register one) goes through the capability registry.
    services.AddWhizbangCapabilities();
    services.AddWhizbangAspNet();
    services.AddWhizbangSagas();
    services.AddWhizbangScope();
    services.AddAzureServiceBusTransport(FAKE_SERVICE_BUS);
    services.AddRabbitMQTransport("amqp://guest:guest@localhost:5672/");
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    services.AddWhizbangAzureBlobArchive(configuration);
    services.AddWhizbangManagedHealth();
    services.AddWhizbangRunControl();
    services.AddWhizbangMessageSecurity();
    services.AddSystemEvents();
    services.AddWhizbangPinnedWorkerPool(_ => { });
    // The table statistics settings are bound by the registration both data drivers call; the provider is
    // the driver's and is never built here.
    Whizbang.Core.Observability.TableStatisticsServiceCollectionExtensions.AddWhizbangTableStatistics(
      services, (_, _) => throw new InvalidOperationException("the manifest harness builds no provider"));
    services.AddGraphQLServer().AddWhizbangStartupStatus();

    await using var provider = services.BuildServiceProvider();
    foreach (var optionsType in _configuredOptionsTypes(services)) {
      var readBefore = new HashSet<string>(recorder.Keys, StringComparer.OrdinalIgnoreCase);
      object? resolved = null;
      _tryResolve(() => {
        var accessor = provider.GetService(typeof(IOptions<>).MakeGenericType(optionsType));
        resolved = accessor?.GetType().GetProperty("Value")?.GetValue(accessor);
        return resolved;
      }, optionsType.Name);
      if (resolved is not null) {
        _resolvedOptions?.Invoke(resolved, [.. recorder.Keys.Where(k => !readBefore.Contains(k))]);
      }
      // Named options bind a section per name: a consumer-chosen name records as '*'.
      _tryResolve(() => {
        var monitor = provider.GetService(typeof(IOptionsMonitor<>).MakeGenericType(optionsType));
        return monitor?.GetType().GetMethod("Get")?.Invoke(monitor, [ConfigurationKeyRecorder.PLACEHOLDER]);
      }, optionsType.Name + " (named)");
    }

    // Services built by a factory that reads configuration: process-wide options, the service
    // identity, the HotChocolate startup-status switch. Anything that could open a connection is
    // left alone; the recorder only needs construction.
    foreach (var serviceType in _factoryBuiltServices(services)) {
      _tryResolve(() => provider.GetService(serviceType), serviceType.Name);
    }

    // The tag policy reads its sections when the host starts, so start it (alone: building every
    // hosted service would need a running host).
    _tryResolve(() => {
      var validator = ActivatorUtilities.CreateInstance<TagPolicyStartupValidator>(provider);
      validator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
      return null;
    }, nameof(TagPolicyStartupValidator));

    // The capability provisioning switch is read per provider and per capability when the Capabilities step runs;
    // a placeholder provider and capability record both levels as consumer-chosen names.
    _tryResolve(() => CapabilityAutoProvision.Resolve(
      new CapabilityId(ConfigurationKeyRecorder.PLACEHOLDER, ConfigurationKeyRecorder.PLACEHOLDER), new CapabilityOptions(), configuration),
      nameof(CapabilityAutoProvision));

    // The managed-object reconcile reads its settings by hand at schema initialization and in its maintenance step.
    _tryResolve(() => Whizbang.Data.Postgres.Schema.ManagedSchemaSettings.Read(configuration), "ManagedSchemaSettings");

    inspect?.Invoke(provider, services);
    return recorder.Keys;
  }

  private static void _tryResolve(Func<object?> resolve, string what) {
    try {
      _ = resolve();
    } catch (Exception ex) when (ex is InvalidOperationException or System.Reflection.TargetInvocationException
                                   or ArgumentException or FormatException or AggregateException
                                   or OptionsValidationException) {
      // Whatever it read before failing is already recorded.
      Console.WriteLine($"{what}: {ex.GetBaseException().Message}");
    }
  }

  private static IEnumerable<Type> _factoryBuiltServices(IServiceCollection services) =>
    services
      .Where(d => d.ImplementationFactory is not null && !d.ServiceType.ContainsGenericParameters
        && d.ServiceType.Namespace?.StartsWith("Whizbang", StringComparison.Ordinal) == true
        && d.ServiceType.Name.EndsWith("Options", StringComparison.Ordinal))
      .Select(d => d.ServiceType)
      .Append(typeof(Whizbang.Core.Observability.IServiceInstanceProvider))
      .Distinct();

  private static IEnumerable<Type> _configuredOptionsTypes(IServiceCollection services) =>
    services
      .Select(d => d.ServiceType)
      .Where(t => t.IsGenericType && !t.ContainsGenericParameters
        && (t.GetGenericTypeDefinition() == typeof(IConfigureOptions<>)
            || t.GetGenericTypeDefinition() == typeof(IPostConfigureOptions<>)))
      .Select(t => t.GetGenericArguments()[0])
      .Distinct();

  /// <summary>
  /// Keys read only where this harness cannot reach: connection strings, read when a data source is
  /// first resolved, and the connection-pool section, read by the code the EF Core generator writes
  /// into the consumer's own assembly.
  /// </summary>
  private static readonly string[] _keysReadOutsideTheHarness = [
    "ConnectionStrings:*",
    "ConnectionPool:MaxPoolSize",
    "ConnectionPool:MinPoolSize",
    "ConnectionPool:Timeout",
    "ConnectionPool:CommandTimeout",
    // Each connection's timeout key (PostgresCommandTimeouts), read where the connection is built: the
    // generated pool, the notification connection, the pinned pool and schema initialization.
    "Whizbang:Postgres:*:CommandTimeoutSeconds",
    // The reconcile's per-kind drop switches and configured pins, read as children of their sections, so a
    // configuration with none records only the section.
    "Whizbang:Schema:Reconcile:Drop:*",
    "Whizbang:Schema:Reconcile:Pins:*",
    // The release lists of the ephemeral blocker report (#2250): a list per ephemeral type, read as children of their
    // section, so a configuration with none records nothing.
    "Whizbang:Ephemeral:ReleaseBlockers:*:*",
    // The application version's host overrides, read when the version is first resolved (the heartbeat, the
    // stand-down beat and the migration ledger), which the harness never reaches; ApplicationVersionTests reads both.
    "Whizbang:Application:DeployNumber",
    "Whizbang:Application:Version",
    // The incompatibility override (#2022), read when a Postgres driver builds its Assess verdict's assessor, which the
    // harness never resolves; UpgradeOrderTests reads it.
    "Whizbang:Startup:OverrideIncompatibility",
    // The lossy-rollback switch (#2122), read beside the override when a Postgres driver builds its assessor;
    // UpgradeOrderTests reads it.
    "Whizbang:Startup:AllowLossyRollback",
  ];

  /// <summary>
  /// Sensitive keys read where the harness cannot see a <see cref="Whizbang.Core.Configuration.SensitiveString"/>
  /// property: every connection string, and the body cipher's key-encryption keys, read by hand and decoded to bytes.
  /// </summary>
  private static readonly string[] _sensitiveKeysReadOutsideTheHarness = [
    "ConnectionStrings:*",
    "Whizbang:BodyOffload:Cipher:KeyEncryptionKey",
    "Whizbang:BodyOffload:Cipher:PreviousKeyEncryptionKey",
  ];

  private const string SENTINEL = "sentinel:";

  private const string SENSITIVE_HEADER =
    "# Every configuration key whose value is a credential: bound to a SensitiveString, a connection string, or a key read\n"
    + "# by hand. The startup settings report redacts each by its type.\n"
    + "# Generated by ConfigurationKeyManifestTests (WHIZBANG_UPDATE_CONFIGURATION_KEYS=1); do not edit by hand.";

  [Test]
  public async Task EverySensitiveSetting_BindsFromConfiguration_AndIsInTheSensitiveKeyListAsync() {
    var keys = await RecordKeysTheLibraryReadsAsync();
    var sensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await _composeLibraryAsync(null, (_, services) => {
      foreach (var type in _configuredOptionsTypes(services)) {
        sensitiveNames.UnionWith(_sensitiveProperties(type).Select(p => p.Name));
      }
    });

    // Answer every key that may feed a SensitiveString property with a value naming the key, then read the values back.
    var values = keys
      .Where(key => sensitiveNames.Contains(key[(key.LastIndexOf(':') + 1)..]))
      .ToDictionary(
        key => key.Replace("*", ConfigurationKeyRecorder.PLACEHOLDER, StringComparison.Ordinal),
        key => SENTINEL + key,
        StringComparer.OrdinalIgnoreCase);
    var bound = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    var unbound = new List<string>();
    await _composeLibraryAsync(values, (provider, services) => {
      foreach (var type in _configuredOptionsTypes(services)) {
        var properties = _sensitiveProperties(type).ToList();
        if (properties.Count == 0) {
          continue;
        }
        var instances = new[] { Options.DefaultName, ConfigurationKeyRecorder.PLACEHOLDER }
          .Select(name => _configure(provider, type, name)).OfType<object>().ToList();
        foreach (var property in properties) {
          var read = instances.Select(i => property.GetValue(i)).OfType<Whizbang.Core.Configuration.SensitiveString>()
            .Select(s => s.Reveal()).Where(v => v.StartsWith(SENTINEL, StringComparison.Ordinal)).ToList();
          if (read.Count == 0) {
            unbound.Add(type.Name + "." + property.Name);
          }
          bound.UnionWith(read.Select(v => v[SENTINEL.Length..]));
        }
      }
    });

    await Assert.That(unbound).IsEmpty()
      .Because("a SensitiveString setting the configuration binder source generator skips must be bound with "
        + "ConfigurationValueBinder.BindSensitive: " + string.Join(", ", unbound));

    var sensitive = bound.Concat(_sensitiveKeysReadOutsideTheHarness)
      .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var path = Path.Combine(Path.GetDirectoryName(_manifestPath())!, "sensitive-configuration-keys.txt");
    if (Environment.GetEnvironmentVariable("WHIZBANG_UPDATE_CONFIGURATION_KEYS") == "1") {
      await File.WriteAllTextAsync(path, SENSITIVE_HEADER + "\n" + string.Join("\n", sensitive) + "\n");
    }
    var listed = (await File.ReadAllLinesAsync(path)).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    await Assert.That(listed).IsEquivalentTo(sensitive)
      .Because("the settings bound to a SensitiveString changed; regenerate Baselines/sensitive-configuration-keys.txt "
        + "with WHIZBANG_UPDATE_CONFIGURATION_KEYS=1");
    await Assert.That(sensitive).Contains("Whizbang:Archive:AzureBlob:ConnectionString");
  }

  private const string DEFAULTS_HEADER =
    "# The value each configuration key takes when nothing sets it, read from a freshly constructed instance of the options\n"
    + "# type its section binds. The startup settings report shows it, marked (default).\n"
    + "# Generated by ConfigurationKeyManifestTests (WHIZBANG_UPDATE_CONFIGURATION_KEYS=1); do not edit by hand.";

  [Test]
  public async Task TheReportedDefaults_AreTheDefaultsInCodeAsync() {
    var manifested = new HashSet<string>(
      (await File.ReadAllLinesAsync(_manifestPath())).Where(l => l.Length > 0 && !l.StartsWith('#')), StringComparer.OrdinalIgnoreCase);
    var defaults = await _recordDefaultsAsync(manifested);
    var path = Path.Combine(Path.GetDirectoryName(_manifestPath())!, "configuration-defaults.txt");
    var expected = defaults.Select(d => d.Key + "=" + d.Value).ToList();
    if (Environment.GetEnvironmentVariable("WHIZBANG_UPDATE_CONFIGURATION_KEYS") == "1") {
      await File.WriteAllTextAsync(path, DEFAULTS_HEADER + "\n" + string.Join("\n", expected) + "\n");
    }
    var listed = (await File.ReadAllLinesAsync(path)).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    await Assert.That(expected.Count).IsGreaterThan(100).Because("the defaults must be read, or the comparison proves nothing");
    await Assert.That(expected).Contains("Whizbang:Workers:CoalescingOutbox:Partitions=4");
    await Assert.That(listed).IsEquivalentTo(expected)
      .Because("a default in code changed; regenerate Baselines/configuration-defaults.txt with WHIZBANG_UPDATE_CONFIGURATION_KEYS=1");
  }

  /// <summary>Composes the library and records, for every manifest key it reads, the value its options type holds.</summary>
  private static async Task<SortedDictionary<string, string>> _recordDefaultsAsync(HashSet<string> manifested) {
    var defaults = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // Each options type is resolved over a configuration that holds nothing, so what it holds is what the service runs
    // with when nothing is set; each key it read is found on it by the longest property path that ends the key.
    _resolvedOptions = (instance, keys) => {
      foreach (var key in keys.Where(k => manifested.Contains(k) && !k.Contains('*', StringComparison.Ordinal))) {
        var segments = key.Split(':');
        for (var start = 1; start < segments.Length; start++) {
          if (_walk(instance, segments[start..], out var value)) {
            if (_format(value) is { } formatted) {
              defaults.TryAdd(key, formatted);
            }
            break;
          }
        }
      }
    };
    try {
      await RecordKeysTheLibraryReadsAsync();
    } finally {
      _resolvedOptions = null;
    }
    return defaults;
  }

  /// <summary>
  /// Follows <paramref name="segments"/> through public properties (by name or configuration key name); false when a
  /// segment names no property.
  /// </summary>
  private static bool _walk(object? instance, string[] segments, out object? value) {
    value = instance;
    foreach (var segment in segments) {
      if (value is null) {
        return false;
      }
      var property = value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .FirstOrDefault(p => p.GetIndexParameters().Length == 0
          && string.Equals(
            (p.GetCustomAttributes(typeof(ConfigurationKeyNameAttribute), true).FirstOrDefault() as ConfigurationKeyNameAttribute)?.Name ?? p.Name,
            segment, StringComparison.OrdinalIgnoreCase));
      if (property is null) {
        return false;
      }
      value = property.GetValue(value);
    }
    return true;
  }

  /// <summary>A default as configuration would spell it; null for no value or one that is not a single setting.</summary>
  private static string? _format(object? value) => value switch {
    null => null,
    bool flag => flag ? "true" : "false",
    string text => text,
    Enum member => member.ToString(),
    Whizbang.Core.Configuration.SensitiveString => null,
    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
    _ => null,
  };

  private static IEnumerable<System.Reflection.PropertyInfo> _sensitiveProperties(Type type) =>
    type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
      .Where(p => p.PropertyType == typeof(Whizbang.Core.Configuration.SensitiveString) && p.CanRead);

  /// <summary>
  /// A new <paramref name="type"/> with every configure step registered for it applied under <paramref name="name"/>,
  /// without validation (a validator may refuse what the sentinel values make of it); null when it cannot be created.
  /// </summary>
  private static object? _configure(IServiceProvider provider, Type type, string name) {
    if (type.GetConstructor(Type.EmptyTypes) is null) {
      return null;
    }
    var instance = Activator.CreateInstance(type)!;
    foreach (var step in provider.GetServices(typeof(IConfigureOptions<>).MakeGenericType(type)).OfType<object>()) {
      _tryResolve(() => {
        var named = typeof(IConfigureNamedOptions<>).MakeGenericType(type);
        if (named.IsInstanceOfType(step)) {
          named.GetMethod("Configure", [typeof(string), type])!.Invoke(step, [name, instance]);
        } else if (name == Options.DefaultName) {
          typeof(IConfigureOptions<>).MakeGenericType(type).GetMethod("Configure")!.Invoke(step, [instance]);
        }
        return null;
      }, type.Name + " configure");
    }
    return instance;
  }

  private const string MANIFEST_HEADER =
    "# Every configuration key the Whizbang library reads, one per line. '*' is a name the consumer chooses.\n"
    + "# Generated by ConfigurationKeyManifestTests (WHIZBANG_UPDATE_CONFIGURATION_KEYS=1); do not edit by hand.\n"
    + "# The documentation site fails its build on any documented key that is not listed here.";

  private static string _manifestPath() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }

    return Path.Combine(
      dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary."),
      "tests", "Whizbang.Documentation.Tests", "Baselines", "configuration-keys.txt");
  }

  [Test]
  public async Task TheLibraryReadsExactlyTheManifestedKeysAsync() {
    var recorded = (await RecordKeysTheLibraryReadsAsync())
      .Concat(_keysReadOutsideTheHarness)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Order(StringComparer.OrdinalIgnoreCase)
      .ToList();
    var path = _manifestPath();

    if (Environment.GetEnvironmentVariable("WHIZBANG_UPDATE_CONFIGURATION_KEYS") == "1") {
      await File.WriteAllTextAsync(path, MANIFEST_HEADER + "\n" + string.Join("\n", recorded) + "\n");
    }

    var manifested = (await File.ReadAllLinesAsync(path))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .ToList();
    var added = recorded.Except(manifested, StringComparer.OrdinalIgnoreCase).ToList();
    var removed = manifested.Except(recorded, StringComparer.OrdinalIgnoreCase).ToList();

    await Assert.That(added.Count + removed.Count).IsEqualTo(0)
      .Because("the library's configuration keys changed. Read now but not in Baselines/configuration-keys.txt: ["
        + string.Join(", ", added) + "]. Listed but no longer read: [" + string.Join(", ", removed) + "]. "
        + "Regenerate with WHIZBANG_UPDATE_CONFIGURATION_KEYS=1 and update the documentation to match.");
  }
}
