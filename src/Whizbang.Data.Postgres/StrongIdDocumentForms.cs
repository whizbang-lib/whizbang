// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The internal members of <see cref="WhizbangIdDocumentForm"/>, for the drivers' tests: the write form's setters are
/// internal to Whizbang so consumer code cannot write the UUID string ahead of the fleet, and a driver's test project
/// sees this assembly's internals but not Whizbang.Core's (#2518).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
internal static class StrongIdDocumentForms {
  /// <inheritdoc cref="WhizbangIdDocumentForm.ScopeTo(bool)"/>
  public static IDisposable ScopeTo(bool uuidString) => WhizbangIdDocumentForm.ScopeTo(uuidString);

  /// <inheritdoc cref="WhizbangIdDocumentForm.Hold(string?, string?)"/>
  public static void Hold(string? database, string? schema) => WhizbangIdDocumentForm.Hold(database, schema);

  /// <inheritdoc cref="WhizbangIdDocumentForm.Open(string?, string?)"/>
  public static void Open(string? database, string? schema) => WhizbangIdDocumentForm.Open(database, schema);

  /// <inheritdoc cref="WhizbangIdDocumentForm.IsOpen(string?, string?)"/>
  public static bool IsOpen(string? database, string? schema) => WhizbangIdDocumentForm.IsOpen(database, schema);

  /// <inheritdoc cref="WhizbangIdDocumentForm.IsHeld(string?, string?)"/>
  public static bool IsHeld(string? database, string? schema) => WhizbangIdDocumentForm.IsHeld(database, schema);
}
