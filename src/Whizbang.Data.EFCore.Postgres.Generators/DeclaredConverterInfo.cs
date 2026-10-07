// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// A converter a perspective model declares on one of its values with
/// <c>[JsonConverter(typeof(...))]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Carried as names rather than as symbols, like every other discovered fact here: an incremental
/// generator's values have to be equatable and cacheable, and a symbol is neither.
/// </para>
/// <para>
/// The names become a closed generic call the compiler emits —
/// <c>DeclaredJsonConverterConvention.Apply&lt;TValue, TConverter&gt;(modelBuilder, "Name")</c> — which is
/// what lets a declared converter reach a mapped document at all without reflection. Reading the
/// attribute at run time would need it, and this assembly's output is ahead-of-time compatible.
/// </para>
/// </remarks>
/// <param name="PropertyName">The value's name on its model.</param>
/// <param name="ValueTypeName">Fully qualified type of the value, unwrapped from Nullable&lt;T&gt;.</param>
/// <param name="ConverterTypeName">Fully qualified type of the declared converter.</param>
internal sealed record DeclaredConverterInfo(
    string PropertyName,
    string ValueTypeName,
    string ConverterTypeName
);
