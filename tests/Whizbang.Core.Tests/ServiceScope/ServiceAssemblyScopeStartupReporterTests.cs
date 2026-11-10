// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// The startup report names, at Debug, the service a host anchored on and the assemblies it included and excluded
/// (#2243).
/// </summary>
/// <code-under-test>src/Whizbang.Core/ServiceAssemblyScopeStartupReporter.cs</code-under-test>
public class ServiceAssemblyScopeStartupReporterTests {
  private static readonly Dictionary<string, ServiceAssemblyFacts> _facts = new(StringComparer.Ordinal) {
    ["Shared.Contracts"] = new("Shared.Contracts", false, []),
    ["Orders"] = new("Orders", true, ["Shared.Contracts"]),
    ["Billing"] = new("Billing", true, ["Shared.Contracts"]),
  };

  private static async Task<FakeLogRecord> _reportAsync(ServiceAssemblyScope scope) {
    var logger = new FakeLogger<ServiceAssemblyScopeStartupReporter>();
    var reporter = new ServiceAssemblyScopeStartupReporter(scope, logger);

    await reporter.StartAsync(CancellationToken.None);
    await reporter.StopAsync(CancellationToken.None);

    return logger.Collector.GetSnapshot().Single();
  }

  [Test]
  public async Task AScopedHost_IsReportedAtDebug_NamingAnchorsIncludedAndExcludedAsync() {
    var record = await _reportAsync(ServiceAssemblyScope.Compute(["Orders"], _facts));

    await Assert.That(record.Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(record.Message).Contains("[Orders]");
    await Assert.That(record.Message).Contains("included [Orders, Shared.Contracts]");
    await Assert.That(record.Message).Contains("excluded as another service's [Billing]");
  }

  [Test]
  public async Task AnUnscopedHost_IsReportedAtDebug_WithTheReasonAsync() {
    var record = await _reportAsync(ServiceAssemblyScope.Compute([], _facts));

    await Assert.That(record.Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(record.Message).Contains("not scoped to a service");
    await Assert.That(record.Message).Contains("names no service");
  }
}
