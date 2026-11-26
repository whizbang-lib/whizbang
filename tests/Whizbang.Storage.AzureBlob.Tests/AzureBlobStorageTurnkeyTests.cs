// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Offloads;

namespace Whizbang.Storage.AzureBlob.Tests;

/// <summary>
/// The package is turnkey (#2502): referenced and configured, with no call, <c>AddWhizbang()</c> archives and offloads
/// through Azure Blob Storage. An explicit registration wins whether it comes before or after, the package registers
/// once, and the opt-out keeps the package loaded and the defaults in place.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#turnkey-activation</docs>
[NotInParallel] // AddWhizbang runs the process-wide storage provider registrations
public class AzureBlobStorageTurnkeyTests {
  private const string EMULATOR = "UseDevelopmentStorage=true";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  private static readonly (string, string)[] _configured = [
    ("Whizbang:Storage:AzureBlob:ConnectionString", EMULATOR),
    ("Whizbang:Storage:AzureBlob:Offloads:ContainerName", "bodies"),
    ("Whizbang:Storage:AzureBlob:Archive:ContainerName", "from-config"),
  ];

  [Test]
  public async Task Turnkey_APackageThatIsReferencedAndConfigured_ArchivesAndOffloadsThroughBlob_WithoutACallAsync() {
    // This assembly names no package type before here: the generated module initializer registered it (#2502).
    await Assert.That(ServiceRegistrationCallbacks.StorageProviders).Contains(AzureBlobArchiveObjectStore.PROVIDER_NAME);
    var services = new ServiceCollection();
    services.AddSingleton(_config(_configured));
    services.AddWhizbang();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ArchiveObjectStoreSelection.Resolve(provider)).IsTypeOf<AzureBlobArchiveObjectStore>()
      .Because("the archive's object store is Azure Blob, so the driver's coordinator writes through it");
    await Assert.That(provider.GetRequiredKeyedService<IMessageBodyStore>("azure-blob")).IsTypeOf<AzureBlobMessageBodyStore>();
    await Assert.That(provider.GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value.ProviderName).IsEqualTo("azure-blob");
  }

  [Test]
  public async Task Turnkey_OptedOut_KeepsThePackageLoaded_AndTheDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(_configured));
    services.AddWhizbang(o => o.AutoRegisterStorageProviders = false);
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ServiceRegistrationCallbacks.StorageProviders).Contains(AzureBlobArchiveObjectStore.PROVIDER_NAME);
    await Assert.That(provider.GetService<ArchiveObjectStoreSelection>()).IsNull().Because("the driver keeps its table store");
    await Assert.That(provider.GetKeyedService<IMessageBodyStore>("azure-blob")).IsNull();
    await Assert.That(provider.GetService<IOptions<MessageBodyOffloadOptions>>()?.Value.ProviderName).IsNull();
  }

  [Test]
  public async Task ExplicitRegistration_WinsOverConfiguration_BeforeOrAfterAddWhizbangAsync() {
    var before = new ServiceCollection();
    before.AddSingleton(_config(_configured));
    before.AddWhizbangAzureBlobStorage(o => o.Archive!.ContainerName = "from-code");
    before.AddWhizbang();
    var after = new ServiceCollection();
    after.AddSingleton(_config(_configured));
    after.AddWhizbang();
    after.AddWhizbangAzureBlobStorage(o => o.Archive!.ContainerName = "from-code");
    await using var first = before.BuildServiceProvider();
    await using var second = after.BuildServiceProvider();

    await Assert.That(ArchiveObjectStoreSelection.Resolve(first)!.Location.Container).IsEqualTo("from-code");
    await Assert.That(ArchiveObjectStoreSelection.Resolve(second)!.Location.Container).IsEqualTo("from-code")
      .Because("code configures the options after configuration does, in either order");
    await Assert.That(before.Count(d => d.ServiceType == typeof(ArchiveObjectStoreSelection))).IsEqualTo(1)
      .Because("the package registers once however often it is asked");
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobStorage()).Throws<ArgumentNullException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAzureBlobStorage(_ => { })).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangAzureBlobStorage((Action<AzureBlobStorageOptions>)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ExplicitRegistration_InCode_TurnsAFeatureOn_WithNoConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobStorage(o => {
      o.ConnectionString = EMULATOR;
      o.Archive = new AzureBlobArchiveOptions { ContainerName = "code" };
    });
    await using var provider = services.BuildServiceProvider();

    var selection = provider.GetRequiredService<ArchiveObjectStoreSelection>();

    await Assert.That(selection.Store!.Location.Container).IsEqualTo("code");
    await Assert.That(selection.Reason).IsEqualTo("azure-blob, account from code");
  }

  [Test]
  public async Task ThePackage_DeclaresItselfTurnkey_NamingTheRegistrarTheGeneratedInitializerCallsAsync() {
    // The generators read this assembly attribute from every reference and call the registrar it names from the
    // consumer's module initializer, so a package no code names still registers itself (#2502).
    var declared = typeof(AzureBlobStorageTurnkey).Assembly
      .GetCustomAttributes(typeof(WhizbangTurnkeyPackageAttribute), inherit: false)
      .Cast<WhizbangTurnkeyPackageAttribute>()
      .ToArray();

    await Assert.That(declared).Count().IsEqualTo(1);
    await Assert.That(declared[0].Registrar).IsEqualTo(typeof(AzureBlobStorageTurnkey));
    await Assert.That(new WhizbangTurnkeyPackageAttribute(typeof(string)).Registrar).IsEqualTo(typeof(string))
      .Because("the attribute carries exactly the registrar it was given");
  }

  [Test]
  public async Task Register_IsSafeToCallAgainAsync() {
    AzureBlobStorageTurnkey.Register();
    AzureBlobStorageTurnkey.Register();

    await Assert.That(ServiceRegistrationCallbacks.StorageProviders.Count(p => p == AzureBlobArchiveObjectStore.PROVIDER_NAME)).IsEqualTo(1);
  }
}
