// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.ConventionIds.Fixtures;

/// <summary>
/// An id nothing loads before an Entity Framework model that holds it in a document is built through
/// <c>WhizbangIdConvention.Apply</c> on a context without the options extension, so its assembly's module initializer
/// has not recorded its stored form by then (#2518). In an assembly of its own so that test starts from an initializer
/// not yet run, whatever ran first. Only WhizbangIdModelTimingTests may use it.
/// </summary>
[WhizbangId]
public readonly partial struct UntouchedConventionOrderId;
