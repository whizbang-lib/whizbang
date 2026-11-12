// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// The service-wide composite batch settings (#2285): bound from <c>Whizbang:CompositeBatches</c>, with a byte limit that,
/// when not set, is derived to stay under the transport's largest message and the body-offload threshold, and reported
/// with its inputs in the startup settings report.
/// </summary>
public class CompositeBatchDefaultsTests {
  private sealed class SizedTransport(long? max) : ITransport {
    public long? MaxMessageSizeBytes => max;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private static ServiceProvider _provider(IReadOnlyDictionary<string, string?>? settings = null, ITransport? transport = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
    if (transport is not null) {
      services.AddSingleton(transport);
    }
    services.AddWhizbang();
    return services.BuildServiceProvider();
  }

  private static IDerivedSetting[] _reported(IServiceProvider provider) =>
    [.. provider.GetServices<IDerivedSettingSource>().SelectMany(s => s.DerivedSettings)
      .Where(d => d.Name == "Whizbang:CompositeBatches:MaxBufferedBytes")];

  [Test]
  public async Task TheSection_BindsEverySetting_AndAConfiguredByteLimitIsNotDerivedAsync() {
    await using var provider = _provider(new Dictionary<string, string?> {
      ["Whizbang:CompositeBatches:MaxBufferedMessages"] = "250",
      ["Whizbang:CompositeBatches:MaxBufferedBytes"] = "20000",
      ["Whizbang:CompositeBatches:DebounceInterval"] = "00:00:00.250",
      ["Whizbang:CompositeBatches:MaxWait"] = "00:00:05",
    });

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedMessages).IsEqualTo(250);
    await Assert.That(options.MaxBufferedBytes).IsEqualTo(20000);
    await Assert.That(options.DebounceInterval).IsEqualTo(TimeSpan.FromMilliseconds(250));
    await Assert.That(options.MaxWait).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(_reported(provider)).IsEmpty();
  }

  [Test]
  public async Task WithNoTransport_TheByteLimitIsThreeQuartersOfTheOffloadThreshold_AndIsReportedAsync() {
    await using var provider = _provider();

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedBytes).IsEqualTo(48L * 1024L);
    await Assert.That(_reported(provider).Single().ToString())
      .IsEqualTo("49152 (0.75 x SizeThresholdBytes (the transport sets no limit); SizeThresholdBytes=65536)");
  }

  [Test]
  public async Task ATransportWithASmallerLimit_SetsTheByteLimitAsync() {
    await using var provider = _provider(transport: new SizedTransport(40L * 1024L));

    await Assert.That(provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value.MaxBufferedBytes).IsEqualTo(30L * 1024L);
  }

  [Test]
  [Arguments(256L * 1024L, 49152L)]
  [Arguments(40L * 1024L, 30720L)]
  public async Task TheDerivation_TakesTheSmallerOfTheThresholdAndTheTransportLimitAsync(long transportMax, long expected) {
    var derived = CompositeBatchDefaults.Derive(64L * 1024L, transportMax);

    await Assert.That(derived.Value).IsEqualTo(expected);
    await Assert.That(derived.Inputs.Select(i => i.Name)).IsEquivalentTo(["SizeThresholdBytes", "TransportMaxMessageSizeBytes"]);
  }

  [Test]
  public async Task ATransportWithNoLimit_LeavesTheOffloadThresholdAsync() {
    await using var provider = _provider(transport: new SizedTransport(null));

    await Assert.That(provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value.MaxBufferedBytes).IsEqualTo(48L * 1024L);
  }

  [Test]
  public async Task WithoutConfiguration_TheCodeDefaultsApplyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedMessages).IsNull();
    await Assert.That(options.DebounceInterval).IsEqualTo(TimeSpan.FromMilliseconds(100));
  }
}
