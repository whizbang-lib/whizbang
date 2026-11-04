// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// #2027: the core registration supplies the application version every instance records, so a host that configures
/// nothing still records one (or "unknown"), and a host's own provider wins.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/ApplicationVersionRegistration.cs</code-under-test>
[Category("Versioning")]
public class ApplicationVersionRegistrationTests {
  [Test]
  public async Task TheRegistration_ResolvesFromConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection([new KeyValuePair<string, string?>(ApplicationVersion.DEPLOY_NUMBER_KEY, "5")]).Build());

    ApplicationVersionRegistration.TryAddWhizbangApplicationVersion(services);
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IApplicationVersionProvider>().ApplicationVersion.Recorded).IsEqualTo("deploy:5");
  }

  [Test]
  public async Task AHostsOwnProvider_WinsAsync() {
    var mine = new ApplicationVersionProvider(ApplicationVersion.FromRecorded("7.0.0"));
    var services = new ServiceCollection();
    services.AddSingleton<IApplicationVersionProvider>(mine);

    ApplicationVersionRegistration.TryAddWhizbangApplicationVersion(services);
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IApplicationVersionProvider>()).IsSameReferenceAs(mine);
  }

  [Test]
  public async Task TheCorePipeline_RegistersItAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

    services.AddWhizbang();

    await Assert.That(services.Any(d => d.ServiceType == typeof(IApplicationVersionProvider))).IsTrue();
  }

  [Test]
  public async Task EntryInformationalVersion_OfNoAssembly_IsNullAsync() {
    await Assert.That(ApplicationVersionRegistration.EntryInformationalVersion(null)).IsNull();
  }

  [Test]
  public async Task EntryInformationalVersion_ReadsTheAttributeAsync() {
    var version = ApplicationVersionRegistration.EntryInformationalVersion(typeof(ApplicationVersion).Assembly);

    await Assert.That(version).IsNotNull();
  }

  [Test]
  public async Task EntryInformationalVersion_OfAnAssemblyWithoutTheAttribute_IsNullAsync() {
    // A dynamic assembly declares no attributes at all, like a host built without SDK version stamping.
    var bare = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
      new System.Reflection.AssemblyName("whizbang-bare-" + (Guid)TrackedGuid.New()), System.Reflection.Emit.AssemblyBuilderAccess.Run);

    await Assert.That(ApplicationVersionRegistration.EntryInformationalVersion(bare)).IsNull()
      .Because("an entry assembly without an informational version yields none, so the application version is unknown");
  }

  [Test]
  public async Task TheRegistration_WithNullServices_ThrowsAsync() {
    await Assert.That(() => ApplicationVersionRegistration.TryAddWhizbangApplicationVersion(null!)).Throws<ArgumentNullException>();
  }
}
