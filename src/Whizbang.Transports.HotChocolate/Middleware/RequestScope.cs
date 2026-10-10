// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// The scope of the request a resolver is running for.
/// </summary>
/// <remarks>
/// The scope middleware publishes the request's scope through <see cref="IScopeContextAccessor.Current"/>, so
/// that is where it is read. Resolving <see cref="IScopeContext"/> instead depends on which registrations the
/// host made: with only the scope services there is no such registration, and with the message-security
/// services an unscoped request throws instead of reading as a caller with no permissions.
/// </remarks>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/RequirePermissionMiddlewareTests.cs</tests>
internal static class RequestScope {
  /// <summary>
  /// The request's scope, or <see langword="null"/> when the request has none or no accessor is registered.
  /// </summary>
  internal static IScopeContext? Resolve(IServiceProvider services) =>
    services.GetService<IScopeContextAccessor>()?.Current;
}
