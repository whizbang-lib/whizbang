// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.MultiStream;

/// <summary>Registers multi-stream command handlers.</summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:AddMultiStreamCommandHandler_RegistersOneBindingNamedForTheCommandTypeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:AddMultiStreamCommandHandler_ASecondHandlerForTheSameCommand_IsRefusedAsync</tests>
public static class MultiStreamCommandServiceCollectionExtensions {
  /// <summary>
  /// Registers <typeparamref name="THandler"/> as the one handler of <typeparamref name="TCommand"/>, the command's
  /// binding, and its intake receptor. Calling it again with the same handler does nothing.
  /// </summary>
  /// <typeparam name="TCommand">The multi-stream command type.</typeparam>
  /// <typeparam name="THandler">Its handler, resolved per stream from a scope.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <exception cref="InvalidOperationException">A different handler is already registered for the command.</exception>
  public static IServiceCollection AddMultiStreamCommandHandler<TCommand,
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services)
      where TCommand : IMultiStreamCommand
      where THandler : class, IMultiStreamCommandHandler<TCommand> {
    ArgumentNullException.ThrowIfNull(services);
    foreach (var descriptor in services) {
      if (descriptor.ImplementationInstance is HandlerRegistration existing && existing.Command == typeof(TCommand)) {
        if (existing.Handler != typeof(THandler)) {
          throw new InvalidOperationException(
            $"{MultiStreamCommandBinding<TCommand>.TYPE_NAME} already has a handler ({existing.Handler.Name}); a multi-stream command has exactly one, so {typeof(THandler).Name} cannot be added.");
        }
        return services;
      }
    }

    services.AddSingleton(new HandlerRegistration(typeof(TCommand), typeof(THandler)));
    services.TryAddScoped<THandler>();
    services.TryAddScoped<IMultiStreamCommandHandler<TCommand>>(sp => sp.GetRequiredService<THandler>());
    services.AddSingleton<IMultiStreamCommandBinding>(new MultiStreamCommandBinding<TCommand>());
    services.TryAddSingleton<IMultiStreamCommandStore>(NullMultiStreamCommandStore.Instance);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MultiStreamCommandReceptorRegistrar>());
    return services;
  }

  /// <summary>Records which handler a command was registered with, so a second, different one is refused.</summary>
  private sealed record HandlerRegistration(Type Command, Type Handler);
}
