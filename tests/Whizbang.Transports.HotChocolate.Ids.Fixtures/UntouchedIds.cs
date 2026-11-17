// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Ids.Fixtures;

/// <summary>
/// An id nothing loads before a GraphQL schema that takes it is built, so its assembly's module initializer has not
/// recorded it in the registry by then. Only WhizbangIdSchemaTimingTests may use it.
/// </summary>
[WhizbangId]
public readonly partial struct UntouchedOrderId;
