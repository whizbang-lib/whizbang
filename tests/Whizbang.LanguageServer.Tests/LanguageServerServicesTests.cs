using Microsoft.Extensions.DependencyInjection;
using Whizbang.LanguageServer;
using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests;

/// <summary>
/// Tests for language server service registration.
/// </summary>
/// <remarks>
/// The entry point binds stdin/stdout and blocks on the server's lifetime, so nothing inside it
/// can be reached from a test. What can break there is the registration list, and it breaks
/// silently: a handler class that exists but is never registered means an editor feature that
/// simply never responds — no exception, no log, and no other test in the suite would notice.
/// </remarks>
/// <tests>Whizbang.LanguageServer/LanguageServerServices.cs:*</tests>
[NotInParallel("LanguageServerDocsEnv")]
public class LanguageServerServicesTests {

  private static ServiceProvider _build(string docsBaseUrl = "https://example.test")
    => new ServiceCollection().AddLanguageServerServices(docsBaseUrl).BuildServiceProvider();

  [Test]
  [Arguments(typeof(DebugSessionHandler))]
  [Arguments(typeof(SearchHandler))]
  [Arguments(typeof(SymbolHandler))]
  [Arguments(typeof(TestCoverageHandler))]
  [Arguments(typeof(FlowDiagramHandler))]
  [Arguments(typeof(StatusHandler))]
  public async Task EveryHandler_IsResolvableAsync(Type handlerType) {
    // Each handler backs one editor capability. An unregistered one is not a crash -- the
    // request just goes unanswered, which reads to a user as the feature not existing.
    using var provider = _build();

    await Assert.That(provider.GetService(handlerType)).IsNotNull()
      .Because($"{handlerType.Name} backs an editor capability that is dead without it");
  }

  [Test]
  [Arguments(typeof(MermaidGenerator))]
  [Arguments(typeof(SymbolResolver))]
  [Arguments(typeof(SearchService))]
  [Arguments(typeof(TestCoverageService))]
  [Arguments(typeof(DebugSessionManager))]
  public async Task EverySupportingService_IsResolvableAsync(Type serviceType) {
    using var provider = _build();

    await Assert.That(provider.GetService(serviceType)).IsNotNull();
  }

  [Test]
  [Arguments(typeof(DebugSessionManager))]
  [Arguments(typeof(SearchService))]
  public async Task StatefulServices_AreSingletonsAsync(Type serviceType) {
    // These carry state across requests: DebugSessionManager holds live debug sessions, and
    // SearchService holds the built Lucene index. Registered per-request they would lose that
    // state on every call -- sessions would vanish and every search would rebuild the index.
    using var provider = _build();

    var first = provider.GetService(serviceType);
    var second = provider.GetService(serviceType);

    await Assert.That(first).IsNotNull();
    await Assert.That(ReferenceEquals(first, second)).IsTrue()
      .Because($"{serviceType.Name} carries state between requests and must not be rebuilt per call");
  }

  [Test]
  public async Task ResolveDocsBaseUrl_WithoutTheEnvironmentVariable_UsesThePublicSiteAsync() {
    // Every "go to docs" link in the editor is built from this. An unset variable has to fall
    // back to the real site rather than produce links to nowhere.
    var original = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL");
    try {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", null);

      await Assert.That(LanguageServerServices.ResolveDocsBaseUrl())
        .IsEqualTo(LanguageServerServices.DefaultDocsBaseUrl);
    } finally {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", original);
    }
  }

  [Test]
  public async Task ResolveDocsBaseUrl_WithAnOverride_UsesItAsync() {
    // The override exists so a docs preview or an internal mirror can be pointed at.
    var original = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL");
    try {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", "https://docs.internal.test");

      await Assert.That(LanguageServerServices.ResolveDocsBaseUrl())
        .IsEqualTo("https://docs.internal.test");
    } finally {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", original);
    }
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ResolveDocsBaseUrl_WithABlankOverride_FallsBackAsync(string blank) {
    // An empty variable is what an unset shell export or a misconfigured launch profile
    // produces. Treating it as a real value would build every docs link off "".
    var original = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL");
    try {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", blank);

      await Assert.That(LanguageServerServices.ResolveDocsBaseUrl())
        .IsEqualTo(LanguageServerServices.DefaultDocsBaseUrl);
    } finally {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL", original);
    }
  }

  [Test]
  public async Task AddLanguageServerServices_RejectsABlankDocsUrlAsync() {
    await Assert.That(() => new ServiceCollection().AddLanguageServerServices("  "))
      .Throws<ArgumentException>();
  }
}
