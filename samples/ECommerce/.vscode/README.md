# Running Aspire ECommerce Sample in VS Code

## Quick Start - Press F5!

The launch.json is configured with two working options:

1. **Launch Aspire AppHost** - Simple `dotnet` type launcher (requires C# Dev Kit)
2. **Launch Aspire AppHost (coreclr)** - Explicit coreclr launcher with all environment variables

Press **F5** and select your preferred configuration.

## Available Tasks

All tasks available via **Terminal → Run Task** or **Ctrl+Shift+P → Tasks: Run Task**:

- **`run AppHost`** - Launch via terminal (no debugging)
- **`build`** - Build entire solution
- **`build AppHost`** - Build just the AppHost
- **`restore`** - Restore NuGet packages
- **`clean`** - Clean build artifacts
- **`watch AppHost`** - Hot reload mode

## What You Get

When you run the AppHost, Aspire starts:
- **Aspire Dashboard** at https://localhost:17286 or http://localhost:15195
- **OrderService.API** (REST API with weather forecast endpoint)
- **InventoryWorker** (background service)
- **NotificationWorker** (background service)
- **PaymentWorker** (background service)
- **ShippingWorker** (background service)

The dashboard provides:
- Real-time service health monitoring
- Distributed tracing
- Logs from all services
- Resource management
- Environment variables

## Debugging

With F5 debugging enabled, you can:
- Set breakpoints in the AppHost or any service
- Step through code
- Inspect variables
- Use the full VS Code debugging experience

## Requirements

Make sure you have installed:
- **C# Dev Kit** extension (ms-dotnettools.csdevkit)
- **C#** extension (ms-dotnettools.csharp)
- **.NET 9 SDK**
- **Aspire workload** (`dotnet workload install aspire`)

## Troubleshooting

If F5 hangs or doesn't work:
1. Ensure C# Dev Kit is installed and up to date
2. Try the "coreclr" configuration instead of "dotnet"
3. Use the **"run AppHost"** task as a fallback (runs without debugging)
4. Check Output panel (View → Output) and select "C# Dev Kit" for error messages

## Configuration Details

The launch configurations pull settings from:
- `ECommerce.AppHost/Properties/launchSettings.json` - Port configuration
- `.vscode/launch.json` - Debugger settings
- `.vscode/tasks.json` - Build tasks
