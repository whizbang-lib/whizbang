// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Orders a receptor among the receptors this service runs for the same message (#1412). Every local
/// receptor runs, on every call; lower values run first. A receptor without this attribute runs at
/// <see cref="ReceptorPriority.DEFAULT"/>, the middle of the range, so a receptor can be placed before or
/// after the ones that declare nothing.
/// </summary>
/// <remarks>
/// <para>When a caller asks for a result (<c>LocalInvokeAsync&lt;TResult&gt;</c>) and several receptors
/// return one, the earliest by priority answers the caller; the others still run and their results
/// cascade. Receptors with the same priority run in declaration order.</para>
/// <para>The order holds among the receptors of one assembly. Receptors from other assemblies, then
/// receptors registered at runtime, run after this assembly's own.</para>
/// </remarks>
/// <param name="priority">The receptor's priority. See <see cref="ReceptorPriority"/> for named values.</param>
/// <docs>fundamentals/receptors/receptor-priority</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/EveryReceptorInPriorityOrderComponentTests.cs:EveryEntry_RunsEveryLocalReceptorInPriorityOrderAsync</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ReceptorPriorityAttribute(int priority) : Attribute {
  /// <summary>The receptor's priority; lower runs first.</summary>
  public int Priority { get; } = priority;
}

/// <summary>Named receptor priorities for <see cref="ReceptorPriorityAttribute"/>. Lower runs first.</summary>
/// <docs>fundamentals/receptors/receptor-priority</docs>
public static class ReceptorPriority {
  /// <summary>Runs before every other priority.</summary>
  public const int FIRST = 0;

  /// <summary>Runs before the default.</summary>
  public const int EARLY = 250;

  /// <summary>The priority of a receptor that declares none: the middle of the range.</summary>
  public const int DEFAULT = 500;

  /// <summary>Runs after the default.</summary>
  public const int LATE = 750;

  /// <summary>Runs after every other priority.</summary>
  public const int LAST = 1000;
}
