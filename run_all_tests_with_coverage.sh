#!/bin/bash
set -e

echo "=== Running All Tests with Coverage ===" 
rm -rf coverage_results
mkdir -p coverage_results

# List of test projects
projects=(
  "Whizbang.Core.Tests"
  "Whizbang.Observability.Tests"
  "Whizbang.Policies.Tests"
  "Whizbang.Execution.Tests"
  "Whizbang.Partitioning.Tests"
  "Whizbang.Transports.Tests"
  "Whizbang.Sequencing.Tests"
  "Whizbang.Data.Tests"
  "Whizbang.Data.Postgres.Tests"
)

total=0
passed=0
failed=0

for project in "${projects[@]}"; do
  echo "Running: $project"
  cd "tests/$project"
  
  # Run with coverage
  dotnet run -- --coverage --coverage-output-format cobertura --coverage-output "../../coverage_results/${project}.cobertura.xml" > "../../coverage_results/${project}.log" 2>&1
  exit_code=$?
  
  if [ $exit_code -eq 0 ]; then
    echo "  ✓ Passed"
    ((passed++))
  else
    echo "  ✗ Failed (exit code: $exit_code)"
    ((failed++))
    # Show last 20 lines of log on failure
    tail -20 "../../coverage_results/${project}.log"
  fi
  ((total++))
  
  cd - > /dev/null
done

echo ""
echo "=== Test Summary ==="
echo "Total Projects: $total"
echo "Passed: $passed"
echo "Failed: $failed"

# Merge coverage files
echo ""
echo "=== Merging Coverage Files ==="
cd coverage_results
coverage_files=$(ls *.cobertura.xml 2>/dev/null | tr '\n' ' ')
if [ -n "$coverage_files" ]; then
  echo "Found coverage files: $coverage_files"
  # Copy first file as base, we'll analyze each separately
  echo "Coverage files ready for analysis"
else
  echo "No coverage files found"
fi
cd ..
