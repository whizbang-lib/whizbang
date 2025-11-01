# Testing Guide

## Current Status: TUnit + VS Code

As of October 2025, TUnit has **known limitations** with VS Code Test Explorer integration. While TUnit officially supports VS Code, test discovery in the Test Explorer panel is not consistently working due to ongoing Microsoft Testing Platform integration issues.

## ✅ What Works

TUnit tests work perfectly from the command line:

```bash
# Run all tests
dotnet test

# Run tests in a specific project
dotnet test tests/Whizbang.Core.Tests

# Watch mode (re-run on file changes)
dotnet watch test --project tests/Whizbang.Core.Tests

# List all tests
dotnet test --list-tests
```

## 🚀 Running Tests in VS Code

We've configured VS Code tasks for easy test execution:

### Keyboard Shortcuts

- **Run All Tests**: `Cmd+Shift+B` (or `Ctrl+Shift+B`) → Select "test: all"
- **Run Build**: `Cmd+Shift+B` → Select "build" (default)

### Using Command Palette

1. Press `Cmd+Shift+P` (or `Ctrl+Shift+P`)
2. Type "Tasks: Run Task"
3. Select one of:
   - **test: all** - Run all tests in solution
   - **test: Core.Tests** - Run only Whizbang.Core.Tests
   - **test: watch** - Run tests in watch mode (re-run on changes)

### Using Terminal

Simply open the integrated terminal (`Ctrl+``) and run:

```bash
dotnet test
```

## 📊 Test Output

TUnit provides beautiful output in the terminal:

```
████████╗██╗   ██╗███╗   ██╗██╗████████╗
╚══██╔══╝██║   ██║████╗  ██║██║╚══██╔══╝
   ██║   ██║   ██║██╔██╗ ██║██║   ██║
   ██║   ██║   ██║██║╚██╗██║██║   ██║
   ██║   ╚██████╔╝██║ ╚████║██║   ██║
   ╚═╝    ╚═════╝ ╚═╝  ╚═══╝╚═╝   ╚═╝
```

Shows clear pass/fail status, execution time, and detailed error messages.

## 🧪 Test Structure

### Current Test Coverage (v0.1.0)

**Whizbang.Core.Tests** - 17 tests total:

- **ReceptorTests.cs** (8 tests)
  - Type-safe interface validation
  - Async operation support
  - Multi-destination routing
  - Stateless operation
  - Flexible response types (single, tuple, array)
  - Validation and business logic
  - Error handling

- **DispatcherTests.cs** (9 tests)
  - Message routing
  - Context preservation
  - Event publishing
  - Batch operations (SendMany)
  - Causation chain tracking
  - Handler not found exceptions
  - Correct handler routing
  - Multi-destination support

## 🎯 TDD Workflow

We're following Test-Driven Development (Red-Green-Refactor):

### Current Phase: 🔴 RED

All tests are intentionally failing with `NotImplementedException`:

```csharp
public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
    throw new NotImplementedException("OrderReceptor not yet implemented");
}
```

This is **expected and correct** - tests define the required behavior before implementation.

### Next Phase: 🟢 GREEN

Implement the minimum code to make tests pass:
1. Implement `InMemoryDispatcher`
2. Implement receptor test handlers
3. Verify all tests pass

### Final Phase: 🔵 REFACTOR

Optimize and clean up the implementation while keeping tests green.

## 🛠️ Troubleshooting

### VS Code Test Explorer Not Showing Tests

This is a **known issue** with TUnit and VS Code as of October 2025. We've configured everything correctly:

✅ Added `<OutputType>Exe</OutputType>` to test projects
✅ Removed `Microsoft.NET.Test.Sdk` (blocks TUnit)
✅ Using unified `TUnit` package
✅ Enabled `dotnet.testWindow.useTestingPlatformProtocol` setting
✅ C# Dev Kit extension recommended

**Workaround**: Use the VS Code tasks or terminal commands above.

### Tests Failing with NotImplementedException

This is **expected** during the TDD red phase. All 17 tests should fail until we implement the production code.

### Build Errors

If you see build errors, try:

```bash
dotnet clean
dotnet restore
dotnet build
```

## 📚 Additional Resources

- [TUnit Documentation](https://tunit.dev/)
- [TUnit GitHub](https://github.com/thomhurst/TUnit)
- [Microsoft Testing Platform](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-platform-intro)
