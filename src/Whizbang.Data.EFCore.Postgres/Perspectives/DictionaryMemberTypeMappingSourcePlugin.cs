// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore.Storage;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Answers Entity Framework's type mapping question for every registered dictionary type with
/// <see cref="DictionaryMemberTypeMapping"/>, so a dictionary inside a perspective document is a value rather than a
/// navigation Entity Framework refuses (#1911).
/// </summary>
/// <remarks>
/// A type mapping rather than a convention: Entity Framework asks the type mapping source while it discovers a complex
/// type's members, which is where it decides a dictionary is a navigation and fails. A convention runs after that.
/// Rides the Whizbang options extension, which every generated perspective context carries; a context built without it
/// refuses a model holding a dictionary, in Entity Framework's own words, naming the member.
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberStorageTests.cs</tests>
public sealed class DictionaryMemberTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin {
  /// <inheritdoc/>
  public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo) =>
    mappingInfo.ClrType is { } clrType ? DictionaryMemberTypeMapping.For(clrType) : null;
}
