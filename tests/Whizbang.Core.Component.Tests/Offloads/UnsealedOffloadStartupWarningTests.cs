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
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Offloaded bodies are stored in the clear unless a cipher is named. Encryption stays opt-in, so
/// a host that offloads without one is told so once, at start, before any body leaves the process.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/UnsealedOffloadStartupWarning.cs</code-under-test>
/// <docs>fundamentals/offloads/message-body-store#sealing-bodies</docs>
public class UnsealedOffloadStartupWarningTests {
  private static async Task<IReadOnlyList<FakeLogRecord>> _startAsync(params (string Key, string Value)[] settings) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(settings.ToDictionary(s => "Whizbang:BodyOffload:" + s.Key, s => (string?)s.Value))
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging(b => b.AddFakeLogging());
    services.AddWhizbangBodyOffload();
    await using var provider = services.BuildServiceProvider();

    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StartAsync(CancellationToken.None);
    }

    return provider.GetFakeLogCollector().GetSnapshot()
      .Where(r => r.Category == typeof(UnsealedOffloadStartupWarning).FullName)
      .ToList();
  }

  [Test]
  public async Task AStoreWithNoCipher_WarnsOnceAtStartAsync() {
    var records = await _startAsync(("ProviderName", "blob"));

    await Assert.That(records).Count().IsEqualTo(1);
    await Assert.That(records[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(records[0].Message).Contains("blob");
    await Assert.That(records[0].Message).Contains("CipherName");
  }

  [Test]
  public async Task AStoreWithACipher_IsQuietAsync() {
    var records = await _startAsync(("ProviderName", "blob"), ("CipherName", "aes-gcm"));

    await Assert.That(records).IsEmpty()
      .Because("bodies are sealed before upload");
  }

  [Test]
  public async Task NoStore_IsQuietAsync() {
    var records = await _startAsync();

    await Assert.That(records).IsEmpty()
      .Because("without a store nothing is offloaded, so nothing is stored in the clear");
  }

  [Test]
  public async Task RegisteringOffloadTwice_StillWarnsOnceAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangBodyOffload();
    services.AddWhizbangBodyOffload();

    await Assert.That(services.Count(d => d.ImplementationType == typeof(UnsealedOffloadStartupWarning))).IsEqualTo(1);
  }

  [Test]
  public async Task Stop_DoesNothingAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangBodyOffload();
    await using var provider = services.BuildServiceProvider();
    var warning = provider.GetServices<IHostedService>().OfType<UnsealedOffloadStartupWarning>().Single();

    await Assert.That(warning.StopAsync(CancellationToken.None).IsCompletedSuccessfully).IsTrue();
  }
}
