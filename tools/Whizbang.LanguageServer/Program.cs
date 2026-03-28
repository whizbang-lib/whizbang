using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

var docsBaseUrl = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_BASE_URL")
    ?? "https://whizbang-lib.github.io";

var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput())
    .ConfigureLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
    })
    .WithServices(services => {
      // Services
      services.AddSingleton<MermaidGenerator>();
      services.AddSingleton(new SymbolResolver(docsBaseUrl));
      services.AddSingleton<SearchService>();
      services.AddSingleton<TestCoverageService>();

      // Debug
      services.AddSingleton<DebugSessionManager>();

      // Handlers
      services.AddSingleton<DebugSessionHandler>();
      services.AddSingleton<SearchHandler>();
      services.AddSingleton<SymbolHandler>();
      services.AddSingleton<TestCoverageHandler>();
      services.AddSingleton<FlowDiagramHandler>();
      services.AddSingleton<StatusHandler>();
    })
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
