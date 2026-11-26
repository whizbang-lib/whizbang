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

namespace Whizbang.Storage.AzureBlob.Integration.Tests;

/// <summary>
/// The package's startup warnings (#2502): each key of an earlier package that is still read, and each feature configured
/// with no account, is logged once as the host starts. Starts the hosted service, so it lives with the host-level tests.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#migrating-from-the-earlier-packages</docs>
public class AzureBlobStorageStartupNoticesTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  [Test]
  public async Task TheStartupNotices_AreLoggedOnce_EachAsAWarningAsync() {
    var logger = new FakeLogger<AzureBlobStorageStartupNotices>();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(_config(("Whizbang:Archive:AzureBlob:ConnectionString", "UseDevelopmentStorage=true")));
    services.AddSingleton<ILogger<AzureBlobStorageStartupNotices>>(logger);
    services.AddWhizbangAzureBlobStorage(_config(("Whizbang:Archive:AzureBlob:ContainerName", "given")));
    await using var provider = services.BuildServiceProvider();
    var notices = provider.GetServices<IHostedService>().OfType<AzureBlobStorageStartupNotices>().Single();

    await notices.StartAsync(CancellationToken.None);
    await notices.StopAsync(CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot()).Count().IsEqualTo(1)
      .Because("the same warning from the host's configuration and a given one is logged once");
    await Assert.That(logger.LatestRecord.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.LatestRecord.Message).Contains("Whizbang:Archive:AzureBlob is read for compatibility");
  }
}
