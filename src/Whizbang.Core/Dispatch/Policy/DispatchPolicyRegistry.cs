// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>Resolves dispatch policies by id: the built-ins and a developer's own.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#custom-policies</docs>
public interface IDispatchPolicyRegistry {
  /// <summary>Returns the policy with the given id.</summary>
  /// <param name="id">The policy's id.</param>
  /// <returns>The policy.</returns>
  /// <exception cref="InvalidOperationException">No policy has that id.</exception>
  DispatchPolicy GetPolicy(DispatchPolicyId id);

  /// <summary>Adds a developer's own policy. Its id must be at least <see cref="DispatchPolicyId.FIRST_CUSTOM_VALUE"/> and unused.</summary>
  /// <param name="policy">The policy.</param>
  void Register(DispatchPolicy policy);
}

/// <summary>
/// The default <see cref="IDispatchPolicyRegistry"/>. Built-in policies resolve by array index and a
/// developer's own from a concurrent dictionary, so a lookup allocates nothing.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#custom-policies</docs>
public sealed class DispatchPolicyRegistry : IDispatchPolicyRegistry {
  private static readonly DispatchPolicy?[] _builtIns = _indexBuiltIns();
  private readonly ConcurrentDictionary<DispatchPolicyId, DispatchPolicy> _custom = new();

  /// <inheritdoc />
  public DispatchPolicy GetPolicy(DispatchPolicyId id) {
    if (id.Value >= 0 && id.Value < _builtIns.Length && _builtIns[id.Value] is { } builtIn) {
      return builtIn;
    }
    return _custom.TryGetValue(id, out var custom)
      ? custom
      : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
          $"No dispatch policy is registered with id {id.Value}. Register a custom policy through IDispatchPolicyRegistry.Register before selecting it."));
  }

  /// <inheritdoc />
  public void Register(DispatchPolicy policy) {
    ArgumentNullException.ThrowIfNull(policy);
    if (!policy.Id.IsCustom) {
      throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
        $"A custom dispatch policy's id must be {DispatchPolicyId.FIRST_CUSTOM_VALUE} or more; {policy.Id.Value} is reserved for built-in policies."), nameof(policy));
    }
    if (!_custom.TryAdd(policy.Id, policy)) {
      throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
        $"A dispatch policy with id {policy.Id.Value} is already registered."), nameof(policy));
    }
  }

  private static DispatchPolicy?[] _indexBuiltIns() {
    var index = new DispatchPolicy?[DispatchPolicies.All.Max(p => p.Id.Value) + 1];
    foreach (var policy in DispatchPolicies.All) {
      index[policy.Id.Value] = policy;
    }
    return index;
  }
}
