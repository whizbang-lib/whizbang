// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Adds the <see cref="FieldPermissionJson"/> masking to the host's JSON options after the host's own
/// configuration, so a resolver the host sets is kept and the masking is added to it. FastEndpoints copies
/// these options when it maps its endpoints.
/// </summary>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/FieldPermissionJsonTests.cs</tests>
internal sealed class FieldPermissionJsonOptionsSetup : IPostConfigureOptions<JsonOptions> {
  /// <inheritdoc />
  public void PostConfigure(string? name, JsonOptions options) =>
    options.SerializerOptions.AddWhizbangFieldPermissions();
}
