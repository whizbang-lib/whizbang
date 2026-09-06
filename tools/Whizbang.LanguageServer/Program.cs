using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using Whizbang.LanguageServer;
using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

var docsBaseUrl = LanguageServerServices.ResolveDocsBaseUrl();

var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput())
    .ConfigureLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
    })
    .WithServices(services => services.AddLanguageServerServices(docsBaseUrl))
    .OnInitialize(async (server, request, ct) => {
      var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Whizbang.LSP");
      logger.LogInformation("Whizbang Language Server initializing...");

      if (request.RootUri is not null) {
        logger.LogInformation("Workspace: {Root}", request.RootUri.GetFileSystemPath());
      }
    })
    .OnInitialized(async (server, request, response, ct) => {
      var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Whizbang.LSP");
      logger.LogInformation("Whizbang Language Server ready");
    })
).ConfigureAwait(false);

await server.WaitForExit.ConfigureAwait(false);

/// <summary>
/// 
/// </summary>
/// <remarks>
/// The whole of this file is the process entry point: it binds the LSP server to this process's
/// standard input and output and then blocks on <c>WaitForExit</c> until the editor closes the
/// connection. A test cannot run it — doing so would take over the test host's own console
/// streams and never return.
///
/// Nothing testable is hidden behind it. The one decision it makes,
/// <c>LanguageServerServices.ResolveDocsBaseUrl()</c>, and the registrations in
/// <c>AddLanguageServerServices</c> are both exercised directly by
/// <c>tests/Whizbang.LanguageServer.Tests/LanguageServerServicesTests.cs</c>; what remains here is
/// the wiring that hands those to OmniSharp and the two logging callbacks.
/// </remarks>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
  Justification = "Process entry point: binds the LSP server to this process's stdin/stdout and blocks until exit. "
                + "Cannot run under a test host without taking over its console streams. The decisions it makes are "
                + "covered directly in LanguageServerServicesTests.")]
internal partial class Program { }
