#!/bin/bash

echo "=== Building Solution ==="
dotnet build

echo
echo "=== Running All Tests with Coverage ==="
echo

# Test projects
TEST_PROJECTS=(
  "Whizbang.Core.Tests"
  "Whizbang.Observability.Tests"
  "Whizbang.Policies.Tests"
  "Whizbang.Execution.Tests"
  "Whizbang.Partitioning.Tests"
  "Whizbang.Transports.Tests"
  "Whizbang.Data.Tests"
  "Whizbang.Data.Postgres.Tests"
  "Whizbang.Sequencing.Tests"
)

for project in "${TEST_PROJECTS[@]}"; do
  echo "Running: $project"
  cd "tests/$project"
  
  dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml > /dev/null 2>&1
  
  if [ $? -eq 0 ]; then
    echo "  ✓ Success"
  else
    echo "  ✗ Failed (continuing...)"
  fi
  
  cd - > /dev/null
done

echo
echo "=== Coverage Analysis ==="
python3 analyze_coverage.py
