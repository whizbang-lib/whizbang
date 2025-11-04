#!/bin/bash
# Run all tests for Whizbang project
# For .NET 10 with TUnit, we use 'dotnet run' instead of 'dotnet test'

# Array of test projects
test_projects=(
  "tests/Whizbang.Core.Tests"
  "tests/Whizbang.Policies.Tests"
  "tests/Whizbang.Observability.Tests"
  "tests/Whizbang.Partitioning.Tests"
  "tests/Whizbang.Sequencing.Tests"
  "tests/Whizbang.Generators.Tests"
  "tests/Whizbang.Documentation.Tests"
)

total_tests=0
total_failed=0
total_succeeded=0
build_failed=0

echo "Building and running test projects..."
echo ""

for project in "${test_projects[@]}"; do
  if [ -d "$project" ]; then
    echo "========================================"
    echo "  $project"
    echo "========================================"

    cd "$project"

    # Build and run in one command
    dotnet run
    test_exit_code=$?

    cd - > /dev/null

    if [ $test_exit_code -ne 0 ]; then
      echo ""
      echo "⚠️  Failed: $project"
      ((total_failed++))
    else
      echo ""
      echo "✅ Passed: $project"
      ((total_succeeded++))
    fi
    echo ""
  fi
done

echo "========================================"
echo "Test Summary"
echo "========================================"
echo "Test projects run: $((total_succeeded + total_failed))"
echo "Succeeded: $total_succeeded"
echo "Failed: $total_failed"
echo ""

if [ $total_failed -gt 0 ]; then
  echo "❌ Some test projects failed"
  exit 1
else
  echo "✅ All test projects passed"
  exit 0
fi
