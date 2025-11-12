#!/bin/bash

# Clean previous results
rm -rf TestResults
mkdir -p TestResults

# Test projects to run
TEST_PROJECTS=(
  "tests/Whizbang.Core.Tests/Whizbang.Core.Tests.csproj"
  "tests/Whizbang.Observability.Tests/Whizbang.Observability.Tests.csproj"
  "tests/Whizbang.Policies.Tests/Whizbang.Policies.Tests.csproj"
  "tests/Whizbang.Data.Tests/Whizbang.Data.Tests.csproj"
  "tests/Whizbang.Data.Postgres.Tests/Whizbang.Data.Postgres.Tests.csproj"
  "tests/Whizbang.Sequencing.Tests/Whizbang.Sequencing.Tests.csproj"
)

echo "=== Collecting Coverage for All Test Projects ==="
echo

for project in "${TEST_PROJECTS[@]}"; do
  project_name=$(basename "$project" .csproj)
  echo "Running: $project_name"
  
  cd "$(dirname "$project")"
  dotnet run -- --coverage --coverage-output-format cobertura --coverage-output "../../TestResults/${project_name}.cobertura.xml" > /dev/null 2>&1
  
  if [ $? -eq 0 ]; then
    echo "  ✓ Success"
  else
    echo "  ✗ Failed"
  fi
  
  cd - > /dev/null
  echo
done

echo "Coverage collection complete!"
echo "Coverage files in TestResults/"
ls -1 TestResults/*.xml 2>/dev/null || echo "No coverage files found"
