// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Testcontainers.Azurite;

namespace Whizbang.Archive.AzureBlob.Integration.Tests;

/// <summary>
/// One Azurite container for the test run, started on first use. Each test writes to a container of its own, so tests
/// never see each other's objects.
/// </summary>
public static class AzuriteFixture {
  private static AzuriteContainer? _container;
  private static readonly SemaphoreSlim _initLock = new(1, 1);

  /// <summary>Starts Azurite once and returns its connection string.</summary>
  public static async Task<string> EnsureStartedAsync(CancellationToken cancellationToken = default) {
    await _initLock.WaitAsync(cancellationToken);
    try {
      if (_container is null) {
        // The Azure SDK sends service API versions ahead of the emulator; the tests exercise the store, not the version.
        _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
          .WithCommand("--skipApiVersionCheck")
          .Build();
        await _container.StartAsync(cancellationToken);
      }
      return _container.GetConnectionString();
    } finally {
      _initLock.Release();
    }
  }
}
