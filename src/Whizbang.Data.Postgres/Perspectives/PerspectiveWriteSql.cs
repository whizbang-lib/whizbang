// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>The statements a set-based perspective write is made of.</summary>
internal static class PerspectiveWriteSql {
  /// <summary>The parameters a statement binds once for all its rows.</summary>
  internal const int GROUP_PARAMETERS = 3;

  /// <summary>The parameters an upsert, insert or update binds per row.</summary>
  internal const int ROW_PARAMETERS = 6;

  /// <summary>Whether a store type can be written into a cast.</summary>
  internal static bool IsPlainStoreType(string storeType) => throw new NotImplementedException();

  /// <summary>The parameter for one physical column's value.</summary>
  internal static NpgsqlParameter PhysicalParameter(string name, PerspectivePhysicalValue value) => throw new NotImplementedException();
}
