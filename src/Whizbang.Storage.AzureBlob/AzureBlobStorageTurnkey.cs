// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Whizbang.Core;

[assembly: WhizbangTurnkeyPackage(typeof(Whizbang.Storage.AzureBlob.AzureBlobStorageTurnkey))]

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Makes the package turnkey (#2502): registers <see cref="AzureBlobStorageServiceCollectionExtensions.AddWhizbangAzureBlobStorage(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// with <see cref="ServiceRegistrationCallbacks"/>, which <c>AddWhizbang()</c> runs unless the host opts out. Called
/// when the package loads, and from the generated module initializer of every assembly compiled against it, so a host
/// that references the package and configures it needs no call.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#turnkey-activation</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageTurnkeyTests.cs</tests>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AzureBlobStorageTurnkey {
  /// <summary>Registers the package with <see cref="ServiceRegistrationCallbacks"/>. Safe to call more than once.</summary>
  public static void Register() =>
    ServiceRegistrationCallbacks.RegisterStorageProvider(AzureBlobArchiveObjectStore.PROVIDER_NAME, static services => services.AddWhizbangAzureBlobStorage());

  // CA2255: intentional use of ModuleInitializer in library code, the AOT-safe way to self-register when this assembly
  // loads, without Core referencing it (the ASP.NET hosting precedent).
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  internal static void Initialize() => Register();
}
