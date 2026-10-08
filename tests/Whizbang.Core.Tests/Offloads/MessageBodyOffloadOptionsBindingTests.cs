// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// <see cref="MessageBodyOffloadOptions"/> binds from <c>Whizbang:BodyOffload</c> whenever body
/// offload is registered, so every documented key takes effect whatever provider stores the bodies.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/MessageBodyOffloadOptionsPostConfigure.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#body-offload</docs>
public class MessageBodyOffloadOptionsBindingTests {
  private static MessageBodyOffloadOptions _bound(params (string Key, string Value)[] settings) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(settings.ToDictionary(s => "Whizbang:BodyOffload:" + s.Key, s => (string?)s.Value))
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangBodyOffload();
    return services.BuildServiceProvider().GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value;
  }

  [Test]
  public async Task EveryDocumentedKey_BindsAsync() {
    var options = _bound(
      ("ProviderName", "blob"),
      ("SizeThresholdBytes", "4096"),
      ("ActiveCleanup", "true"),
      ("CipherName", "aes-gcm"),
      ("PassiveExpiry", "7.00:00:00"),
      ("PassiveSweepClaimWindow", "00:30:00"),
      ("PassiveSweepBatchSize", "250"),
      ("PassiveSweepMaxBatchesPerCycle", "4"),
      ("DownloadTimeout", "00:00:45"));

    await Assert.That(options.ProviderName).IsEqualTo("blob");
    await Assert.That(options.SizeThresholdBytes).IsEqualTo(4096L);
    await Assert.That(options.ActiveCleanup).IsTrue();
    await Assert.That(options.CipherName).IsEqualTo("aes-gcm");
    await Assert.That(options.PassiveExpiry).IsEqualTo(TimeSpan.FromDays(7));
    await Assert.That(options.PassiveSweepClaimWindow).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(options.PassiveSweepBatchSize).IsEqualTo(250);
    await Assert.That(options.PassiveSweepMaxBatchesPerCycle).IsEqualTo(4);
    await Assert.That(options.DownloadTimeout).IsEqualTo(TimeSpan.FromSeconds(45));
  }

  [Test]
  public async Task AbsentKeys_KeepTheDefaultsAsync() {
    var defaults = new MessageBodyOffloadOptions();

    var options = _bound();

    await Assert.That(options.ProviderName).IsEqualTo(defaults.ProviderName);
    await Assert.That(options.PassiveExpiry).IsEqualTo(defaults.PassiveExpiry);
    await Assert.That(options.PassiveSweepBatchSize).IsEqualTo(defaults.PassiveSweepBatchSize);
    await Assert.That(options.DownloadTimeout).IsEqualTo(defaults.DownloadTimeout);
  }

  [Test]
  public async Task UnreadableValues_KeepTheDefaultsAsync() {
    var defaults = new MessageBodyOffloadOptions();

    var options = _bound(
      ("SizeThresholdBytes", "big"),
      ("ActiveCleanup", "sometimes"),
      ("PassiveExpiry", "soon"),
      ("PassiveSweepClaimWindow", "later"),
      ("PassiveSweepBatchSize", "many"),
      ("PassiveSweepMaxBatchesPerCycle", "lots"),
      ("DownloadTimeout", "forever"));

    await Assert.That(options.SizeThresholdBytes).IsEqualTo(defaults.SizeThresholdBytes);
    await Assert.That(options.ActiveCleanup).IsEqualTo(defaults.ActiveCleanup);
    await Assert.That(options.PassiveExpiry).IsEqualTo(defaults.PassiveExpiry);
    await Assert.That(options.PassiveSweepClaimWindow).IsEqualTo(defaults.PassiveSweepClaimWindow);
    await Assert.That(options.PassiveSweepBatchSize).IsEqualTo(defaults.PassiveSweepBatchSize);
    await Assert.That(options.PassiveSweepMaxBatchesPerCycle).IsEqualTo(defaults.PassiveSweepMaxBatchesPerCycle);
    await Assert.That(options.DownloadTimeout).IsEqualTo(defaults.DownloadTimeout);
  }

  /// <summary>A host without configuration registered keeps the code defaults rather than failing.</summary>
  [Test]
  public async Task NoConfiguration_KeepsTheDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangBodyOffload();

    var options = services.BuildServiceProvider().GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value;

    await Assert.That(options.DownloadTimeout).IsEqualTo(new MessageBodyOffloadOptions().DownloadTimeout);
  }
}
