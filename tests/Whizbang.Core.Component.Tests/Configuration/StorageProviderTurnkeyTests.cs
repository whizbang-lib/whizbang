// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Configuration;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// Storage provider packages are turnkey (#2502): a loaded package registers itself, <c>AddWhizbang</c> runs it unless the
/// host opts out, and the startup report says which provider each storage feature uses and why, warning when
/// configuration names an account for a package that is not loaded.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#turnkey-activation</docs>
[NotInParallel] // sets the process-wide storage provider registrations, which any test calling AddWhizbang runs
public class StorageProviderTurnkeyTests {
  private const string PROVIDER = "test-storage";

  [Test]
  public async Task AddWhizbang_RunsEveryLoadedStorageProvider_UnlessTheHostOptsOutAsync() {
    var runs = 0;
    ServiceRegistrationCallbacks.RegisterStorageProvider(PROVIDER, _ => runs++);
    try {
      new ServiceCollection().AddWhizbang();
      var byDefault = runs;
      new ServiceCollection().AddWhizbang(o => o.AutoRegisterStorageProviders = false);

      await Assert.That(byDefault).IsEqualTo(1).Because("a loaded package activates without a registration call");
      await Assert.That(runs).IsEqualTo(1).Because("opting out keeps the package loaded but inactive");
      await Assert.That(new WhizbangCoreOptions().AutoRegisterStorageProviders).IsTrue();
    } finally {
      ServiceRegistrationCallbacks.UnregisterStorageProvider(PROVIDER);
    }
  }

  [Test]
  public async Task RegisterStorageProvider_TheSameNameTwice_KeepsOne_AndListsByNameAsync() {
    var ran = new List<string>();
    ServiceRegistrationCallbacks.RegisterStorageProvider("test-b", _ => ran.Add("b"));
    ServiceRegistrationCallbacks.RegisterStorageProvider("test-a", _ => ran.Add("first a"));
    ServiceRegistrationCallbacks.RegisterStorageProvider("test-a", _ => ran.Add("a"));
    try {
      ServiceRegistrationCallbacks.InvokeStorageProviders(new ServiceCollection());

      await Assert.That(ServiceRegistrationCallbacks.StorageProviders).Contains("test-a").And.Contains("test-b");
      await Assert.That(ran.Where(r => r is "a" or "b" or "first a")).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because("a package that loads twice registers once, and providers run in name order");
      await Assert.That(() => ServiceRegistrationCallbacks.RegisterStorageProvider(" ", _ => { })).Throws<ArgumentException>();
      await Assert.That(() => ServiceRegistrationCallbacks.RegisterStorageProvider("x", null!)).Throws<ArgumentNullException>();
    } finally {
      ServiceRegistrationCallbacks.UnregisterStorageProvider("test-a");
      ServiceRegistrationCallbacks.UnregisterStorageProvider("test-b");
    }
  }

  [Test]
  public async Task Reset_ForgetsTheStorageProvidersAsync() {
    ServiceRegistrationCallbacks.RegisterStorageProvider(PROVIDER, _ => { });
    var others = ServiceRegistrationCallbacks.StorageProviders.Where(p => p != PROVIDER).ToList();
    ServiceRegistrationCallbacks.Reset();

    await Assert.That(ServiceRegistrationCallbacks.StorageProviders).IsEmpty();
    await Assert.That(others).IsEmpty().Because("precondition: no storage package is loaded in this test assembly");
  }

  private static async Task<IReadOnlyList<FakeLogRecord>> _reportAsync(Action<IServiceCollection> arrange, params (string Key, string Value)[] settings) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());
    var logger = new FakeLogger<StorageProviderStartupReport>();
    services.AddSingleton<ILogger<StorageProviderStartupReport>>(logger);
    arrange(services);
    await using var provider = services.BuildServiceProvider();
    var report = new StorageProviderStartupReport(provider);
    await report.StartAsync(CancellationToken.None);
    await report.StopAsync(CancellationToken.None);
    return logger.Collector.GetSnapshot();
  }

  [Test]
  public async Task Report_NamesTheArchiveObjectStore_OrTheDriversStore_AndTheOffloadStore_EachWithWhyAsync() {
    var objects = await _reportAsync(s => {
      s.AddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(new NoObjects(), "Whizbang:Storage:AzureBlob:ConnectionString is set"));
      s.AddSingleton<IArchiveStore>(_ => throw new InvalidOperationException("the report never builds the driver's store"));
    });
    var driver = await _reportAsync(s => s.AddSingleton<IArchiveStore>(_ => throw new InvalidOperationException("never built")));
    var driverWithEmptySelection = await _reportAsync(s => {
      s.AddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(null, "Whizbang:Storage:AzureBlob names no account"));
      s.AddSingleton<IArchiveStore>(_ => throw new InvalidOperationException("never built"));
    });
    var offload = await _reportAsync(s => {
      s.Configure<MessageBodyOffloadOptions>(o => o.ProviderName = "azure-blob");
      s.AddKeyedSingleton<IMessageBodyStore>("azure-blob", (_, _) => new NoBodies());
      s.AddSingleton(new StorageFeatureActivation(StorageFeatureActivation.ARCHIVE, "azure-blob", "not this one"));
      s.AddSingleton(new StorageFeatureActivation(StorageFeatureActivation.OFFLOAD, "azure-blob", "Whizbang:Storage:AzureBlob:Offloads is set"));
    });
    var offloadInCode = await _reportAsync(s => s.Configure<MessageBodyOffloadOptions>(o => o.ProviderName = "unregistered"));
    var nothing = await _reportAsync(_ => { });

    await Assert.That(objects.Single().Message).IsEqualTo(
      "Archive store: test, endpoint https://account.example, connection 'default', container 'archive' (Whizbang:Storage:AzureBlob:ConnectionString is set)");
    await Assert.That(driver.Single().Message).IsEqualTo("Archive store: the database driver's built-in store (no archive object store is configured)");
    await Assert.That(driverWithEmptySelection.Single().Message).Contains("(Whizbang:Storage:AzureBlob names no account)");
    await Assert.That(offload.Single().Message).IsEqualTo("Body offload store: provider 'azure-blob', NoBodies (Whizbang:Storage:AzureBlob:Offloads is set)");
    await Assert.That(offloadInCode.Single().Message).IsEqualTo(
      "Body offload store: provider 'unregistered', no store is registered under that name (registered in code)");
    await Assert.That(nothing).IsEmpty().Because("a host with no archive store and no offload has nothing to report");
  }

  [Test]
  [Arguments("Whizbang:Storage:AzureBlob:ConnectionString", "UseDevelopmentStorage=true", "Whizbang:Storage:AzureBlob:ConnectionString")]
  [Arguments("Whizbang:Archive:AzureBlob:ServiceUri", "https://a.blob.core.windows.net", "Whizbang:Archive:AzureBlob:ServiceUri")]
  [Arguments("Whizbang:Offloads:AzureBlob:bodies:ConnectionString", "UseDevelopmentStorage=true", "Whizbang:Offloads:AzureBlob:bodies:ConnectionString")]
  public async Task Report_WarnsWhenConfigurationNamesAnAccount_ForAPackageThatIsNotLoaded_NamingItAsync(string key, string value, string named) {
    var unloaded = await _reportAsync(_ => { }, (key, value));
    ServiceRegistrationCallbacks.RegisterStorageProvider("azure-blob", _ => { });
    IReadOnlyList<FakeLogRecord> loaded;
    try {
      loaded = await _reportAsync(_ => { }, (key, value));
    } finally {
      ServiceRegistrationCallbacks.UnregisterStorageProvider("azure-blob");
    }

    await Assert.That(unloaded.Single().Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(unloaded.Single().Message).Contains(named).And.Contains("SoftwareExtravaganza.Whizbang.Storage.AzureBlob")
      .And.DoesNotContain(value).Because("the warning names the key and the package, never the value, which may be a secret");
    await Assert.That(loaded).IsEmpty().Because("with the package loaded the configuration activates it");
  }

  [Test]
  public async Task NamedAccount_IgnoresBlankValues_AndAHostWithoutConfigurationAsync() {
    var blank = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Storage:AzureBlob:ConnectionString"] = " ",
      ["Whizbang:Offloads:AzureBlob:bodies:ContainerName"] = "c",
    }).Build();
    var keys = StorageProviderStartupReport.KnownPackages.Single().AccountKeys;

    await Assert.That(StorageProviderStartupReport.NamedAccount(blank, keys)).IsNull();
    await Assert.That(StorageProviderStartupReport.NamedAccount(null, keys)).IsNull();
  }

  [Test]
  public async Task AddWhizbang_RegistersTheReportOnce_HoweverOftenItIsCalledAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddWhizbang();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService)
      && d.ImplementationFactory?.Method.ReturnType == typeof(StorageProviderStartupReport))).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbangBodyOffload_Twice_RegistersTheOffloadHookOnceAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangBodyOffload();
    services.AddWhizbangBodyOffload();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IPostSerializeHook))).IsEqualTo(1)
      .Because("a provider activated from its settings and an explicit call both enable offload; a second hook would offload twice");
  }

  private sealed class NoObjects : IArchiveObjectStore {
    public ArchiveLocation Location { get; } = new("test", "https://account.example", "default", "archive");
    public ArchiveObjectStoreSettings Settings { get; } = new();
    public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public IAsyncEnumerable<ArchiveObjectInfo> ListAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ArchiveObjectInfo>();
  }

  private sealed class NoBodies : IMessageBodyStore {
    public string ProviderName => "azure-blob";
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public ValueTask<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
  }
}
