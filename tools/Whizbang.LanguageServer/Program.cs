using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using Whizbang.LanguageServer.Services;

var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput())
    .ConfigureLogging(logging => {
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .WithServices(services => {
        services.AddSingleton<MermaidGenerator>();
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
