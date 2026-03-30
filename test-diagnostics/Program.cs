using Whizbang.Core.Generated;

// Print all collected diagnostics from all generators
_ = WhizbangDiagnostics.Diagnostics(
  categories: DiagnosticCategories.All,
  printToConsole: true
);
