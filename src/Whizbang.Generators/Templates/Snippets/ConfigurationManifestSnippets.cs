// Template snippets for ConfigurationManifestGenerator. Embedded, not compiled: each #region is extracted and its
// placeholders replaced (string placeholders are replaced with escaped string literals).

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Snippets for the configuration manifest: one binding, and one capability requirement inside it.
/// </summary>
public class ConfigurationManifestSnippets {

  /// <summary>One section bound to one options type, its keys and its requirements.</summary>
  public object[] BindingExample() {
    return [
      #region BINDING
      new global::Whizbang.Core.Configuration.ConfigurationBinding(__SECTION__, typeof(__OPTIONS_TYPE__), [__KEYS__], [__REQUIREMENTS__]),
      #endregion
    ];
  }

  /// <summary>One capability requirement.</summary>
  public object RequirementExample() {
    return
      #region REQUIREMENT
      new global::Whizbang.Core.Capabilities.CapabilityRequirement(new global::Whizbang.Core.Capabilities.CapabilityId(__PROVIDER__, __NAME__), __FEATURE__, __KEY__, __VALUE__)
      #endregion
      ;
  }
}
