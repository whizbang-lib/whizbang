// Template snippets for ServiceAssemblyFactsGenerator. Embedded, not compiled: each #region is extracted and its
// placeholders replaced.

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>Snippets for the service assembly facts: one turnkey package's registration.</summary>
public class ServiceAssemblyFactsSnippets {

  /// <summary>One referenced turnkey package's registrar, called when the assembly loads (#2502).</summary>
  public void TurnkeyPackageExample() {
    #region TURNKEY_PACKAGE
    __REGISTRAR__.Register();
    #endregion
  }
}
