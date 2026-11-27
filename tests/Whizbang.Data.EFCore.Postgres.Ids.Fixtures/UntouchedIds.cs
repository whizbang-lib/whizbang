// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Ids.Fixtures;

/// <summary>
/// An id nothing loads before an Entity Framework model that holds it in a document is built through the options
/// extension, so its assembly's module initializer has not recorded its stored form by then (#2518). Only
/// WhizbangIdModelTimingTests may use it.
/// </summary>
[WhizbangId]
public readonly partial struct UntouchedOrderId;
