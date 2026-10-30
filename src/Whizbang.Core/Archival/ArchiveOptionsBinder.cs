// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Archival;

/// <summary>
/// Binds <see cref="ArchiveOptions"/> and <see cref="StreamLifecycleOptions"/> from configuration by hand, so a value the
/// framework cannot use is refused at startup with the key that holds it rather than read as a default.
/// </summary>
/// <docs>fundamentals/events/archive-store#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
internal static class ArchiveOptionsBinder {
  /// <summary>Binds <c>Whizbang:Archive</c>.</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="options">The options to fill.</param>
  internal static void Bind(IConfiguration configuration, ArchiveOptions options) => throw new NotImplementedException();

  /// <summary>Binds <c>Whizbang:StreamLifecycle</c>.</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="options">The options to fill.</param>
  internal static void Bind(IConfiguration configuration, StreamLifecycleOptions options) => throw new NotImplementedException();
}
