// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Observability;

/// <summary>Opts a host in to a trace store.</summary>
public static class TraceStoreRegistration {
  /// <summary>Registers the bounded in-memory trace store.</summary>
  public static IServiceCollection AddWhizbangInMemoryTraceStore(this IServiceCollection services) =>
    throw new NotImplementedException();

  /// <summary>Registers the bounded in-memory trace store with a capacity.</summary>
  public static IServiceCollection AddWhizbangInMemoryTraceStore(this IServiceCollection services, int capacity) =>
    throw new NotImplementedException();
}
