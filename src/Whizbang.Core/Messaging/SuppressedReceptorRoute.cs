// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// How a compiled lifecycle route resolves a receptor declared <see cref="SuppressReceptorRegistrationAttribute"/> (#2309).
/// </summary>
/// <docs>fundamentals/receptors/receptors#unregistered-suppressed-receptor</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/SuppressedReceptorRouteTests.cs</tests>
public static class SuppressedReceptorRoute {
  /// <summary>The logger category the skip is logged under.</summary>
  public const string LOGGER_CATEGORY = "Whizbang.Core.Messaging.SuppressedReceptorRoute";

  /// <summary>Not implemented yet.</summary>
  /// <typeparam name="TService">The receptor interface the route invokes.</typeparam>
  /// <typeparam name="TReceptor">The receptor class the route was compiled for.</typeparam>
  /// <param name="services">The scoped service provider the route runs in.</param>
  /// <param name="key">The key the receptor would be registered under: its class name.</param>
  /// <returns>Nothing yet.</returns>
  public static TService? Resolve<TService, TReceptor>(IServiceProvider services, string key)
      where TService : class
      where TReceptor : class, TService =>
    throw new NotImplementedException(typeof(TReceptor).Name);
}
