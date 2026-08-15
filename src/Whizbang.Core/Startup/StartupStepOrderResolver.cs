using System;
using System.Collections.Generic;

namespace Whizbang.Core.Startup;

/// <summary>
/// Turns declared dependencies into the order startup runs in.
/// </summary>
/// <remarks>
/// <para>
/// This is the point of the pipeline. Today startup order is an emergent property of
/// dependency-injection registration position plus a single boolean gate; here it is a function of
/// what each step says it needs. The same set of steps resolves to the same order however it was
/// registered — if registration order could still influence the result, the pipeline would have
/// reproduced the defect it exists to remove, one layer further down.
/// </para>
/// <para>
/// Independent steps are ordered by name so that ties break the same way every time rather than
/// echoing the order the resolver happened to receive. That is arbitrary but <em>stable</em>, which
/// is the property that matters: a reproducible order is one an operator can reason about.
/// </para>
/// <para>
/// Everything unresolvable throws rather than resolving to something plausible. Picking one of
/// several possible orders and starting up in it is precisely the behaviour being replaced.
/// No reflection, so it is safe under native AOT.
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStepOrderResolverTests.cs</tests>
public static class StartupStepOrderResolver {
  /// <summary>
  /// Resolves <paramref name="steps"/> into the order they must run in, omitting disabled ones.
  /// </summary>
  /// <param name="steps">The registered step descriptors.</param>
  /// <returns>
  /// The enabled steps, every one preceded by everything it declared a dependency on, and
  /// deterministic for a given set regardless of the order supplied.
  /// </returns>
  /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
  /// <exception cref="StartupPipelineConfigurationException">
  /// Two steps share a name; a dependency names no registered step; an enabled step depends on a
  /// disabled one; or the dependencies form a cycle.
  /// </exception>
  public static IReadOnlyList<StartupStepDescriptor> Resolve(
      IReadOnlyCollection<StartupStepDescriptor> steps) {
    ArgumentNullException.ThrowIfNull(steps);

    var byName = new Dictionary<string, StartupStepDescriptor>(steps.Count, StringComparer.Ordinal);
    foreach (var step in steps) {
      if (!byName.TryAdd(step.Name, step)) {
        throw new StartupPipelineConfigurationException(
          $"Startup step '{step.Name}' is registered more than once. Two steps answering to one "
          + "name makes every dependency on it ambiguous, and silently keeping one of them is how "
          + "a step goes missing with nothing reporting it.");
      }
    }

    // Sorting the roots by name is what makes the result independent of registration order: the
    // traversal below is otherwise driven entirely by declared dependencies.
    var ordered = new List<string>(byName.Keys);
    ordered.Sort(StringComparer.Ordinal);

    var result = new List<StartupStepDescriptor>(byName.Count);
    var state = new Dictionary<string, _visitState>(byName.Count, StringComparer.Ordinal);
    var path = new List<string>();

    foreach (var name in ordered) {
      _visit(name, byName, state, path, result);
    }

    return result;
  }

  private enum _visitState { InProgress, Done }

  private static void _visit(
      string name,
      Dictionary<string, StartupStepDescriptor> byName,
      Dictionary<string, _visitState> state,
      List<string> path,
      List<StartupStepDescriptor> result) {

    if (state.TryGetValue(name, out var seen)) {
      if (seen == _visitState.InProgress) {
        var cycle = string.Join(" → ", path) + " → " + name;
        throw new StartupPipelineConfigurationException(
          $"Startup steps form a dependency cycle: {cycle}. No order satisfies it.");
      }
      return;
    }

    var step = byName[name];

    // A disabled step contributes nothing to the order, and nothing may depend on it — that is
    // checked at the dependent, where the error can name both sides.
    if (!step.Enabled) {
      state[name] = _visitState.Done;
      return;
    }

    state[name] = _visitState.InProgress;
    path.Add(name);

    // Dependencies are visited in name order for the same reason the roots are: so the resolved
    // order depends only on what was declared.
    var dependencies = new List<string>(step.DependsOn);
    dependencies.Sort(StringComparer.Ordinal);

    foreach (var dependency in dependencies) {
      if (!byName.TryGetValue(dependency, out var target)) {
        throw new StartupPipelineConfigurationException(
          $"Startup step '{name}' depends on '{dependency}', which is not registered. A dependency "
          + "that names nothing cannot be satisfied, and running anyway would mean starting in an "
          + "order nobody declared.");
      }

      if (!target.Enabled) {
        throw new StartupPipelineConfigurationException(
          $"Startup step '{name}' depends on '{dependency}', which is disabled. Disabling a step "
          + $"must not silently weaken another step's declared ordering — either disable '{name}' "
          + $"too, or leave '{dependency}' enabled.");
      }

      _visit(dependency, byName, state, path, result);
    }

    path.RemoveAt(path.Count - 1);
    state[name] = _visitState.Done;
    result.Add(step);
  }
}
