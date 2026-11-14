// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Messaging;

/// <summary>
/// How a compiled lifecycle route resolves a receptor declared <see cref="SuppressReceptorRegistrationAttribute"/> (#2309).
/// </summary>
/// <remarks>
/// <para>
/// The attribute means "I own this receptor's lifetime", not "ignore this type". The owner registers it by hand in
/// dependency injection, or only at runtime through <see cref="IReceptorRegistry.Register{TMessage}"/>. A route
/// compiled for it therefore finds nothing in the container whenever the owner chose the runtime registry, and that
/// is a supported outcome, not a missing registration: the route skips the receptor and logs the skip once per
/// receptor type at Debug.
/// </para>
/// <para>
/// A receptor without the attribute keeps resolving with <c>GetRequiredService</c>, so a registration that is
/// genuinely missing still fails loudly.
/// </para>
/// <para>
/// Only an instance of the declared receptor class is accepted. The route asks for the class by its key, then for the
/// class itself, then for the message's receptor interface; the last one is taken only when it is that class, because
/// the container returns the last receptor registered for the message, which can be a different receptor.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/receptors#unregistered-suppressed-receptor</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/SuppressedReceptorRouteTests.cs</tests>
public static partial class SuppressedReceptorRoute {
  /// <summary>The logger category the skip is logged under.</summary>
  public const string LOGGER_CATEGORY = "Whizbang.Core.Messaging.SuppressedReceptorRoute";

  private static readonly ConcurrentDictionary<Type, byte> _reported = new();

  /// <summary>
  /// Resolves the receptor a compiled route for a <see cref="SuppressReceptorRegistrationAttribute"/> receptor invokes,
  /// or returns null when the owner registered none in dependency injection.
  /// </summary>
  /// <typeparam name="TService">The receptor interface the route invokes.</typeparam>
  /// <typeparam name="TReceptor">The receptor class the route was compiled for.</typeparam>
  /// <param name="services">The scoped service provider the route runs in.</param>
  /// <param name="key">The key the receptor would be registered under: its class name.</param>
  /// <returns>The owner's instance, or null to skip the receptor.</returns>
  /// <docs>fundamentals/receptors/receptors#unregistered-suppressed-receptor</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SuppressedReceptorRouteTests.cs</tests>
  public static TService? Resolve<TService, TReceptor>(IServiceProvider services, string key)
      where TService : class
      where TReceptor : class, TService {
    ArgumentNullException.ThrowIfNull(services);
    var receptor = services.GetKeyedService<TService>(key)
      ?? services.GetService<TReceptor>()
      ?? (services.GetService<TService>() as TReceptor);
    if (receptor is null) {
      _reportSkipped(services, typeof(TReceptor), typeof(TService));
    }
    return receptor;
  }

  // Marked only once it is logged, so a host with no logger does not use up the one report a later host could make.
  private static void _reportSkipped(IServiceProvider services, Type receptorType, Type serviceType) {
    if (services.GetService<ILoggerFactory>()?.CreateLogger(LOGGER_CATEGORY) is not { } logger
        || !_reported.TryAdd(receptorType, 0)) {
      return;
    }
    Log.Skipped(logger, receptorType, serviceType);
  }

  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Receptor {ReceptorType} is declared [SuppressReceptorRegistration] and no instance of it is registered in dependency injection as {Service}, as its class, or under its class name as a key, so its compiled route skips it. It runs only where its owner registers it at runtime through IReceptorRegistry. Logged once per receptor type.")]
    public static partial void Skipped(ILogger logger, Type receptorType, Type service);
  }
}
