// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Core;
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
public class ConfigurationKeyManifestTests {
  private const string FAKE_SERVICE_BUS =
    "Endpoint=sb://manifest.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=bWFuaWZlc3Q=";

  internal static async Task<IReadOnlyCollection<string>> RecordKeysTheLibraryReadsAsync() {
    // Keys whose value decides whether more keys are read. Each seed opens one gate.
    var recorder = new ConfigurationKeyRecorder(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      // A named cipher makes the cipher's key settings required, so they are read.
      ["Whizbang:BodyOffload:CipherName"] = "aes-gcm",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "manifest-key",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(new byte[32]),
    });
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
    services.AddWhizbangAspNet();
    services.AddWhizbangSagas();
    services.AddWhizbangScope();
    services.AddAzureServiceBusTransport(FAKE_SERVICE_BUS);
    services.AddRabbitMQTransport("amqp://guest:guest@localhost:5672/");
    services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
    services.AddWhizbangManagedHealth();
    services.AddWhizbangRunControl();
    services.AddWhizbangMessageSecurity();
    services.AddSystemEvents();
    services.AddWhizbangPinnedWorkerPool(_ => { });
    services.AddGraphQLServer().AddWhizbangStartupStatus();

    await using var provider = services.BuildServiceProvider();
    foreach (var optionsType in _configuredOptionsTypes(services)) {
      _tryResolve(() => {
        var accessor = provider.GetService(typeof(IOptions<>).MakeGenericType(optionsType));
        return accessor?.GetType().GetProperty("Value")?.GetValue(accessor);
      }, optionsType.Name);
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

    return recorder.Keys;
  }

  private static void _tryResolve(Func<object?> resolve, string what) {
    try {
      _ = resolve();
    } catch (Exception ex) when (ex is InvalidOperationException or System.Reflection.TargetInvocationException
                                   or ArgumentException or FormatException or AggregateException) {
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
  ];

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
