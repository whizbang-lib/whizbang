// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.DependencyInjection;

/// <summary>
/// Lookups for services the framework reads when a host registered them and does without when it
/// did not: a logger, and a bound options value.
/// </summary>
/// <remarks>
/// A worker or registration that runs inside a host it does not control cannot assume the host
/// added logging or the options system. Each lookup's fallback is decided here once, rather than
/// written out (and left untested) at every call site.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs</tests>
internal static class ServiceProviderLookups {
  /// <summary>The registered logger for <typeparamref name="T"/>, or a logger that discards.</summary>
  /// <typeparam name="T">The category type.</typeparam>
  /// <param name="services">The services to read.</param>
  /// <returns>The host's logger, or <see cref="NullLogger{T}.Instance"/> when logging is not registered.</returns>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Logger_Registered_IsTheHostsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Logger_Unregistered_DiscardsAsync</tests>
  internal static ILogger<T> GetLoggerOrNullLogger<T>(this IServiceProvider services) =>
    services.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;

  /// <summary>A logger for <paramref name="category"/> from the registered factory, or a logger that discards.</summary>
  /// <param name="services">The services to read.</param>
  /// <param name="category">The log category.</param>
  /// <returns>The factory's logger, or <see cref="NullLogger.Instance"/> when logging is not registered.</returns>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Category_Registered_IsTheFactorysAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Category_Unregistered_DiscardsAsync</tests>
  internal static ILogger GetLoggerOrNullLogger(this IServiceProvider services, string category) =>
    services.GetService<ILoggerFactory>()?.CreateLogger(category) ?? NullLogger.Instance;

  /// <summary>The bound <typeparamref name="TOptions"/>, or null when the options system is not registered.</summary>
  /// <typeparam name="TOptions">The options type.</typeparam>
  /// <param name="services">The services to read.</param>
  /// <returns>The options value, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Options_Registered_IsTheValueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/DependencyInjection/ServiceProviderLookupsTests.cs:Options_Unregistered_NullAsync</tests>
  internal static TOptions? GetOptionsValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(this IServiceProvider services) where TOptions : class =>
    services.GetService<IOptions<TOptions>>()?.Value;
}
