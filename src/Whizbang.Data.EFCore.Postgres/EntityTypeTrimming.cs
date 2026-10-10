// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The members EF Core's <c>IModel.FindEntityType(Type)</c> requires the trimmer to keep on the type it
/// is given. A helper that forwards a <see cref="Type"/> to it declares the same requirement with this.
/// </summary>
internal static class EntityTypeTrimming {
  /// <summary>The requirement <c>FindEntityType(Type)</c> declares on its argument.</summary>
  internal const DynamicallyAccessedMemberTypes MEMBERS =
    DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors
    | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields
    | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
    | DynamicallyAccessedMemberTypes.Interfaces;
}
