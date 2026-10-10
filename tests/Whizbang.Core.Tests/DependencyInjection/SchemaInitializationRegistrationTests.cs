// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Every driver asks for the one schema initializer the same way, so a host that composes a driver twice, or two
/// drivers, still starts one initializer, and the gate it opens is the one the workers wait on.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationRegistration.cs</code-under-test>
[Category("DependencyInjection")]
public class SchemaInitializationRegistrationTests {

  [Test]
  public async Task AddWhizbangSchemaInitialization_RegistersOneInitializer_EvenWhenCalledTwiceAsync() {
    var services = new ServiceCollection();

    services.AddWhizbangSchemaInitialization();
    services.AddWhizbangSchemaInitialization();

    await using var provider = services.BuildServiceProvider();
    var initializers = provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().ToList();
    await Assert.That(initializers.Count).IsEqualTo(1);
  }
}
