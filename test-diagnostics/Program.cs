using Whizbang.Core.Generated;

// Print all collected diagnostics from all generators
var diagnostics = WhizbangDiagnostics.Diagnostics(
  categories: DiagnosticCategory.All,
  printToConsole: true
);
