// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Registers every multi-stream command's intake receptor at host start. Registration is at run time because the
/// receptor generator sees only the consuming assembly's own receptors.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:Registrar_StartAsync_RegistersTheIntakeAtTheDefaultReceptorStagesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:Registrar_StartAsync_WithTheNullRegistry_RegistersNothingAndStartsAsync</tests>
internal sealed class MultiStreamCommandReceptorRegistrar(
    IServiceProvider services,
    IEnumerable<IMultiStreamCommandBinding> bindings) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    // No registry, or the null default: a host with no dispatcher has nothing to register into and must still start.
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    foreach (var binding in bindings) {
      binding.RegisterIntake(registry, services);
    }
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
