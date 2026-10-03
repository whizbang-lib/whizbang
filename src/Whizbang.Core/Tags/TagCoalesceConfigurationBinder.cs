using Microsoft.Extensions.Configuration;
using Whizbang.Core.Configuration;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tags;

/// <summary>
/// AOT-safe configuration binder for <see cref="TagOptions.CoalesceBindings"/>: the bindable map
/// beside the read-only property. Each child of <c>Whizbang:Tags:Coalesce</c> is a TAG whose keys
/// (<c>SlideSeconds</c>, <c>MaxDelaySeconds</c>, <c>MaxBatchCount</c>, <c>Atomicity</c>,
/// <c>PriorityFold</c>) override that tag's code policy; a configured tag with no code policy
/// gets one built from the defaults. The delegates (<c>CompositeFactory</c>, <c>PriorityFor</c>)
/// are code-only. Idempotent: both consumption seams (the <see cref="CoalesceGroupResolver"/>
/// factory and <see cref="TagPolicyStartupValidator"/>) apply it, whichever runs first.
/// </summary>
/// <docs>operations/configuration/configuration-reference#coalesce-bindings</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagCoalesceConfigurationBinderTests.cs</tests>
internal static class TagCoalesceConfigurationBinder {
  /// <summary>Configuration section the coalesce bindings bind from.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Tags:Coalesce";

  /// <summary>
  /// Applies every configured coalesce policy onto <paramref name="tagOptions"/>. No-ops when
  /// <paramref name="configuration"/> is null or the section is absent.
  /// </summary>
  internal static void Apply(TagOptions tagOptions, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(tagOptions);

    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section?.Exists() != true) {
      return;
    }

    foreach (var tag in section.GetChildren()) {
      var policy = tagOptions.CoalesceBindings.TryGetValue(tag.Key, out var existing)
        ? existing
        : new CoalescePolicyOptions();
      ConfigurationValueBinder.BindInt(tag, nameof(CoalescePolicyOptions.SlideSeconds), v => policy.SlideSeconds = v);
      ConfigurationValueBinder.BindInt(tag, nameof(CoalescePolicyOptions.MaxDelaySeconds), v => policy.MaxDelaySeconds = v);
      ConfigurationValueBinder.BindInt(tag, nameof(CoalescePolicyOptions.MaxBatchCount), v => policy.MaxBatchCount = v);
      ConfigurationValueBinder.BindEnum<FanoutAtomicity>(tag, nameof(CoalescePolicyOptions.Atomicity), v => policy.Atomicity = v);
      ConfigurationValueBinder.BindEnum<CompositePriorityFold>(tag, nameof(CoalescePolicyOptions.PriorityFold), v => policy.PriorityFold = v);
      tagOptions.UseCoalesceBinding(tag.Key, policy);
    }
  }
}
