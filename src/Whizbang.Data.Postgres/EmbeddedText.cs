// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Reads a text resource the package embeds in itself: the migration scripts and the constants file
/// they share.
/// </summary>
/// <remarks>
/// A missing resource is a broken package rather than a condition a caller can recover from, so it
/// is an <see cref="InvalidOperationException"/> naming what is missing. Every embedded read goes
/// through here, so that guard is written, and tested, once.
/// </remarks>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/EmbeddedTextTests.cs</tests>
internal static class EmbeddedText {
  /// <summary>The whole text of <paramref name="resourceName"/> in <paramref name="assembly"/>.</summary>
  /// <param name="assembly">The assembly that embeds the resource.</param>
  /// <param name="resourceName">The manifest resource name.</param>
  /// <param name="missingMessage">What to report when the resource is not there.</param>
  /// <exception cref="InvalidOperationException">The assembly does not embed the resource.</exception>
  // Callers are synchronous public API (IMigrationProvider.GetMigrations/GetMigration and the
  // constants' static initializer); disposing an in-memory manifest resource stream synchronously
  // costs nothing.
#pragma warning disable RCS1261 // Resource can be disposed asynchronously
  internal static string Read(Assembly assembly, string resourceName, string missingMessage) {
    using var stream = assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException(missingMessage);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }
#pragma warning restore RCS1261
}
