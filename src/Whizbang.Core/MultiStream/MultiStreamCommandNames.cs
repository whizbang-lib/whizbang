// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>The one rendering of a multi-stream command's type name: the key a run is recorded and bound under.</summary>
internal static class MultiStreamCommandNames {
  internal static string Of(Type commandType) => TypeNameFormatter.Format(commandType);
}
