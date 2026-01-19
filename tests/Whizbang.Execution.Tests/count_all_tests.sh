#!/bin/bash

echo "=== Counting All Tests ===" 

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

for project in "${projects[@]}"; do
  echo "Counting: $project"
  cd "tests/$project"
  
  # Run and capture results
  output=$(dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml 2>&1)
  
  # Extract test count
  count=$(echo "$output" | grep -E "^  total:" | awk '{print $2}')
  succeeded=$(echo "$output" | grep -E "^  succeeded:" | awk '{print $2}')
  
  if [ -n "$count" ]; then
    echo "  Total: $count, Succeeded: $succeeded"
    total=$((total + count))
  else
    echo "  Could not determine count"
  fi
  
  cd - > /dev/null
done

echo ""
echo "=== Total Tests Across All Projects: $total ==="
