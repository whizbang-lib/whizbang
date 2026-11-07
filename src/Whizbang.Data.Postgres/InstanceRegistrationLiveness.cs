// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>Not yet decided (#2126).</summary>
/// <param name="ConnectionMode">Not yet recorded.</param>
/// <param name="LivenessWindowSeconds">Not yet recorded.</param>
public readonly record struct InstanceRegistrationLiveness(InstanceConnectionMode? ConnectionMode, int? LivenessWindowSeconds) {
  /// <summary>Not yet decided.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>Nothing declared yet.</returns>
  public static InstanceRegistrationLiveness Of(IServiceProvider? services) => services is null ? default : new(null, null);
}
