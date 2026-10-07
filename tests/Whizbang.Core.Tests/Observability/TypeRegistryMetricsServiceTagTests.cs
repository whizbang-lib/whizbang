// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The drift-attribution tag names the service: the configured name when there is one, else the entry
/// assembly's, else a sentinel, so a drift series is never tagged with an empty or missing service.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/TypeRegistryMetrics.cs</code-under-test>
public class TypeRegistryMetricsServiceTagTests {
  [Test]
  public async Task ServiceTag_ConfiguredService_IsUsedAsIsAsync() {
    var tag = TypeRegistryMetrics.ServiceTag("orders", typeof(TypeRegistryMetrics).Assembly);

    await Assert.That(tag.Key).IsEqualTo("service");
    await Assert.That(tag.Value).IsEqualTo("orders");
  }

  [Test]
  public async Task ServiceTag_NoConfiguredService_FallsBackToTheEntryAssemblyNameAsync() {
    var tag = TypeRegistryMetrics.ServiceTag(null, typeof(TypeRegistryMetrics).Assembly);

    await Assert.That(tag.Value).IsEqualTo(typeof(TypeRegistryMetrics).Assembly.GetName().Name);
  }

  [Test]
  public async Task ServiceTag_NoServiceAndNoEntryAssembly_IsTheSentinelAsync() {
    var tag = TypeRegistryMetrics.ServiceTag(null, entryAssembly: null);

    await Assert.That(tag.Value).IsEqualTo("<unknown>")
      .Because("outside a managed host there is no entry assembly, and the tag still needs a value");
  }

  [Test]
  public async Task ServiceTag_EmptyService_IsTheSentinelAsync() {
    var tag = TypeRegistryMetrics.ServiceTag("", entryAssembly: null);

    await Assert.That(tag.Value).IsEqualTo("<unknown>");
  }
}
