// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace Whizbang.Core.Capabilities;

/// <summary>What the verdict does about one required capability in one scope.</summary>
/// <docs>operations/infrastructure/capabilities#validation</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityVerdictTests.cs</tests>
public enum CapabilityVerdictOutcome {
  /// <summary>Required explicitly and Ready: proceed.</summary>
  Proceed,

  /// <summary>Required only implicitly and Ready: the feature uses it.</summary>
  Use,

  /// <summary>Required explicitly and Unknown: warn; the feature fails loudly when it runs if the capability is missing.</summary>
  Warn,

  /// <summary>Required only implicitly and not Ready: the feature degrades, with a warning.</summary>
  Degrade,

  /// <summary>Required explicitly and known not to be Ready: start fails.</summary>
  Fail,
}

/// <summary>One required capability in one scope: its status, every requirement in force on it, and the outcome.</summary>
/// <param name="Status">The capability's status in the scope.</param>
/// <param name="RequiredBy">Every requirement in force on the capability, in the order the sources declared them.</param>
/// <param name="Outcome">What the verdict does about it.</param>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityVerdictTests.cs</tests>
public sealed record CapabilityVerdictEntry(
  CapabilityStatus Status,
  IReadOnlyList<ActiveCapabilityRequirement> RequiredBy,
  CapabilityVerdictOutcome Outcome) {
  /// <summary>Explicit when any requirement in force on it is explicit; otherwise implicit.</summary>
  public CapabilityActivation Activation =>
    RequiredBy.Any(r => r.Activation == CapabilityActivation.Explicit) ? CapabilityActivation.Explicit : CapabilityActivation.Implicit;

  /// <summary>
  /// The verdict line: <c>capability provider/name on 'scope': State[, version][; detail][: reason][. Fix: fix].
  /// Required by: Feature (Key = Value, explicit|default)[; ...]</c>. A capability no provider describes has no scope.
  /// </summary>
  public string Line {
    get {
      var line = new StringBuilder("capability ").Append(Status.Descriptor.Id);
      if (Status.Scope.Length > 0) {
        line.Append(" on '").Append(Status.Scope).Append('\'');
      }
      line.Append(": ").Append(Status.State);
      if (Status.Version is not null) {
        line.Append(", ").Append(Status.Version);
      }
      if (Status.Detail is not null) {
        line.Append("; ").Append(Status.Detail);
      }
      if (Status.Reason is not null) {
        line.Append(": ").Append(Status.Reason);
      }
      if (Status.State != CapabilityState.Ready) {
        line.Append(". Fix: ").Append(_fix(Status));
      }
      return line.Append(". Required by: ").AppendJoin("; ", RequiredBy).ToString();
    }
  }

  // A provisionable capability is usable once provisioned, but nothing has provisioned it yet, so a feature that
  // needs it now cannot run; the descriptor has no fix for a usable state.
  private static string _fix(CapabilityStatus status) =>
    status.State == CapabilityState.Provisionable
      ? $"{status.Descriptor.Id} is available but not installed or enabled; install or enable it"
      : status.Descriptor.Remedy(status.State);
}

/// <summary>
/// The startup verdict over every required capability: one entry per capability and scope that a requirement in
/// force names, with the outcome of the validation table.
/// </summary>
/// <remarks>
/// <para>
/// A capability is satisfied only when it is <see cref="CapabilityState.Ready"/>. An explicit requirement on a
/// capability in any other known state fails start; on <see cref="CapabilityState.Unknown"/> it warns, because
/// the probe could not decide. An implicit requirement on a capability that is not Ready degrades. A capability
/// no registered provider describes reads Unknown, with that as the reason.
/// </para>
/// <para>
/// A requirement with a <see cref="ActiveCapabilityRequirement.Scope"/> is judged only on that scope's status, and
/// reads Unknown there when no provider probes it; one without applies to every scope.
/// </para>
/// <para>
/// Capabilities match without regard to case, as <see cref="ICapabilityRegistry.GetState"/> does.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityVerdictTests.cs</tests>
public sealed class CapabilityVerdict {
  private const string UNDESCRIBED_PURPOSE = "not described by any registered provider";
  private const string UNDESCRIBED_REASON = "no registered provider describes it";
  private const string UNPROBED_SCOPE_REASON = "no registered provider probes this scope";

  private CapabilityVerdict(IReadOnlyList<CapabilityVerdictEntry> entries) {
    Entries = entries;
  }

  /// <summary>The verdict before the <c>Capabilities</c> step has evaluated one: nothing required.</summary>
  public static CapabilityVerdict Empty { get; } = new([]);

  /// <summary>Every required capability in every scope, grouped by capability in the order first required.</summary>
  public IReadOnlyList<CapabilityVerdictEntry> Entries { get; }

  /// <summary>The entries that fail start.</summary>
  public IReadOnlyList<CapabilityVerdictEntry> Failures =>
    [.. Entries.Where(entry => entry.Outcome == CapabilityVerdictOutcome.Fail)];

  /// <summary>Judges every required capability.</summary>
  /// <param name="statuses">The registry's statuses.</param>
  /// <param name="active">The requirements in force.</param>
  /// <returns>The verdict.</returns>
  public static CapabilityVerdict Evaluate(
      IReadOnlyList<CapabilityStatus> statuses, IReadOnlyList<ActiveCapabilityRequirement> active) {
    ArgumentNullException.ThrowIfNull(statuses);
    ArgumentNullException.ThrowIfNull(active);
    var entries = new List<CapabilityVerdictEntry>();
    foreach (var group in active.GroupBy(a => _key(a.Requirement.Capability), StringComparer.Ordinal)) {
      var required = group.ToList();
      var matching = statuses.Where(s => _key(s.Descriptor.Id) == group.Key).ToList();
      var judged = entries.Count;
      foreach (var status in matching) {
        var applicable = required.FindAll(a => a.Scope is null || string.Equals(a.Scope, status.Scope, StringComparison.Ordinal));
        if (applicable.Count > 0) {
          entries.Add(new CapabilityVerdictEntry(status, applicable, _outcome(status.State, _explicitly(applicable))));
        }
      }
      if (entries.Count == judged) {
        // Nothing judged the requirements: no provider describes the capability, or none probes their scope.
        var reason = matching.Count == 0 ? UNDESCRIBED_REASON : UNPROBED_SCOPE_REASON;
        var status = _undescribed(required[0].Requirement.Capability, required[0].Scope ?? string.Empty, reason);
        entries.Add(new CapabilityVerdictEntry(status, required, _outcome(status.State, _explicitly(required))));
      }
    }
    return new CapabilityVerdict(entries);
  }

  /// <summary>Whether any requirement in force names this capability in this scope.</summary>
  /// <param name="status">A status from the registry.</param>
  /// <returns>True when the verdict has an entry for it.</returns>
  public bool IsRequired(CapabilityStatus status) => _entry(status) is not null;

  /// <summary>The gauge's <c>required</c> tag for a status: <c>explicit</c>, <c>implicit</c> or <c>none</c>.</summary>
  /// <param name="status">A status from the registry.</param>
  /// <returns>How the verdict requires it.</returns>
  public string RequiredTag(CapabilityStatus status) => _entry(status) switch {
    null => "none",
    { Activation: CapabilityActivation.Explicit } => "explicit",
    _ => "implicit",
  };

  private CapabilityVerdictEntry? _entry(CapabilityStatus status) {
    ArgumentNullException.ThrowIfNull(status);
    var key = _key(status.Descriptor.Id);
    return Entries.FirstOrDefault(entry =>
      _key(entry.Status.Descriptor.Id) == key && string.Equals(entry.Status.Scope, status.Scope, StringComparison.Ordinal));
  }

  private static string _key(CapabilityId id) => id.ToString().ToUpperInvariant();

  private static CapabilityVerdictOutcome _outcome(CapabilityState state, bool explicitly) =>
    (explicitly, state) switch {
      (true, CapabilityState.Ready) => CapabilityVerdictOutcome.Proceed,
      (true, CapabilityState.Unknown) => CapabilityVerdictOutcome.Warn,
      (true, _) => CapabilityVerdictOutcome.Fail,
      (false, CapabilityState.Ready) => CapabilityVerdictOutcome.Use,
      _ => CapabilityVerdictOutcome.Degrade,
    };

  private static bool _explicitly(List<ActiveCapabilityRequirement> required) =>
    required.Exists(a => a.Activation == CapabilityActivation.Explicit);

  private static CapabilityStatus _undescribed(CapabilityId id, string scope, string reason) =>
    new(new CapabilityDescriptor(id, UNDESCRIBED_PURPOSE, [], Provisionable: false, [], new Dictionary<CapabilityState, string>()),
      scope, CapabilityState.Unknown, Version: null, Detail: null, reason);
}
