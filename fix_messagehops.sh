#!/bin/bash

# Fix all simple inline MessageHop declarations
FILE="tests/Whizbang.Observability.Tests/MessageTracingTests.cs"

# Make backup
cp "$FILE" "$FILE.bak"

# Replace .ServiceName assertions with .ServiceInstance.ServiceName
sed -i '' 's/hop\.ServiceName/hop.ServiceInstance.ServiceName/g' "$FILE"
sed -i '' 's/hop1\.ServiceName/hop1.ServiceInstance.ServiceName/g' "$FILE"
sed -i '' 's/hop2\.ServiceName/hop2.ServiceInstance.ServiceName/g' "$FILE"
sed -i '' 's/hop\.MachineName/hop.ServiceInstance.HostName/g' "$FILE"
sed -i '' 's/causationHops\[\([0-9]\)\]\.ServiceName/causationHops[\1].ServiceInstance.ServiceName/g' "$FILE"
sed -i '' 's/currentHops\[\([0-9]\)\]\.ServiceName/currentHops[\1].ServiceInstance.ServiceName/g' "$FILE"
sed -i '' 's/deserializedCausationHops\[\([0-9]\)\]\.ServiceName/deserializedCausationHops[\1].ServiceInstance.ServiceName/g' "$FILE"

echo "Assertions fixed. Now run manual editing for MessageHop instantiations."
