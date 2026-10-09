#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Finds the test classes in Unit-type test projects that are not unit tests (the purity guard), and writes
    the test-separation inventory (#1264).

.DESCRIPTION
    A unit test runs the code under test in one deterministic flow: an injected fake clock, no background
    threads or hosted workers, and no real I/O. This script scans every C# file of every project that
    declares <WhizbangTestType>Unit</WhizbangTestType> under tests/ and samples/, and classifies each
    top-level type as Unit, Component (real workers, threads or waits in one process), Integration
    (containers or real infrastructure) or Other (real I/O or the real clock without infrastructure).

    How a file is read:
      - Comments, string and character literals, and using directives are blanked first (same length, line
        breaks kept), so a construct named in a doc comment or in C# source held in a string is not matched.
      - The file is split at its top-level type declarations. A type's segment runs from the end of the
        previous top-level type (or of the namespace declaration) to its closing brace, so its attributes
        belong to it and nested types belong to the type that encloses them. A bodiless declaration such as
        a positional record falls into the next segment.
      - Each construct (the $script:PurityRules table) is attributed to the segment it occurs in, with its
        line. A type's category is its worst construct: Integration > Component > Other > Unit.
      - Test classes are types with at least one [Test] attribute; the rest are helpers. Both are reported.
      - A type that names a helper of the same project inherits the helper's category, to a fixed point, so
        a test using a fixture or worker double defined elsewhere is classified by what that double does.
        The names that carried a category are listed as Via. Not followed: a name more than one top-level
        type in the project declares (the scan cannot tell which is meant), a name the type declares itself
        (a nested type shadowing a helper of the same name), and a test class (other tests reference one
        for a nested data type, not for its behavior).
      - The parts of a partial class (same namespace and name, in several files) take the category of the
        whole class, since they move together; each part lists "partial <Class> (<Category>)" in Via.

    Two judgments, both deliberate:
      - Wall-clock reads (DateTime.UtcNow, DateTimeOffset.UtcNow) are not flagged: in these projects they
        make timestamps for test data, and they make a flow timing-dependent only when compared with
        elapsed time, which Stopwatch and TimeProvider.System cover.
      - A bare existence probe (File.Exists, Directory.Exists) is not flagged: the shared compile helpers
        use one to find a reference assembly in the test's own output folder, which no test can race on.
        Every other file-system call is.
      - Constructing an Azure SDK client (ServiceBusClient, BlobServiceClient) is not flagged: it never
        dials, and the tests that do it are DI-registration tests. A RabbitMQ ConnectionFactory or an
        Npgsql connection is, since the tests that make one hand it to code that dials it.
      - SQLite is not flagged: these projects use it in memory (Data Source=:memory:), an in-process
        engine with no I/O.

    Report mode (the default) prints the report and fails only when $script:PurityEnforced is $true; phase 3
    of #1264 sets it once every non-unit test has moved. With -Inventory it writes the CSV and regenerates the
    part of the markdown below $script:GeneratedMarker, keeping the hand-written text above it.

    .github/scripts/tests/Get-TestPurity.Tests.ps1 tests every function and runs this scan over the
    repository.

.PARAMETER Root
    The repository root. Default: two levels above this script.

.PARAMETER Inventory
    Write the inventory instead of printing the report.

.PARAMETER CsvPath
    Where -Inventory writes the CSV. Default: plans/test-separation-inventory.csv under the root.

.PARAMETER MarkdownPath
    Where -Inventory writes the markdown. Default: plans/test-separation-inventory.md under the root.

.EXAMPLE
    pwsh .github/scripts/Get-TestPurity.ps1

.EXAMPLE
    pwsh .github/scripts/Get-TestPurity.ps1 -Inventory
#>

param(
  [Parameter()] [string]$Root = (Join-Path -Path $PSScriptRoot -ChildPath '../..'),
  [Parameter()] [switch]$Inventory,
  [Parameter()] [string]$CsvPath = '',
  [Parameter()] [string]$MarkdownPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Report mode until every non-unit test has moved out of the Unit projects. Phase 3 of #1264 sets this to
# $true, and the repository test in Get-TestPurity.Tests.ps1 then fails on any violation.
$script:PurityEnforced = $false

$script:CategoryRank = @{ Unit = 0; Other = 1; Component = 2; Integration = 3 }
$script:CategoryOrder = @('Unit', 'Component', 'Integration', 'Other')

$script:GeneratedMarker = '<!-- Generated by .github/scripts/Get-TestPurity.ps1 -Inventory. Everything below this line is regenerated; edit above it. -->'

# Comments, then verbatim, raw, regular and interpolated strings, then character literals. Leftmost match
# wins, so a quote inside a comment or a character literal never opens a string.
$script:NoisePattern = [regex]::new('//[^\n]*|/\*[\s\S]*?\*/|\$*@\$*"(?:[^"]|"")*"|\$*"""[\s\S]*?"""|\$*"(?:[^"\\\n]|\\.)*"|''(?:[^''\\\n]|\\.)+''')
$script:UsingPattern = [regex]::new('^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?[\w.]+(?:[ \t]*=[ \t]*[\w.<>, \t]+)?[ \t]*;',
  [System.Text.RegularExpressions.RegexOptions]::Multiline)

# A namespace declaration (group 1 = its name), a type declaration up to its body or its semicolon (group 2
# = the type's name), or a brace. "record" followed by an identifier also matches a variable named record
# (foreach (var record in ...)), which only ever pushes a nested frame and keeps the braces balanced.
$script:StructurePattern = [regex]::new('\bnamespace\s+([\w.]+)\s*[{;]|\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?!where\b)(\w+)[^{};]*[{;]|[{}]')

$script:TestAttributePattern = [regex]::new('\[\s*Test\s*[\],(]')

# The non-unit constructs, in report order. Each maps to the category it puts a test in. A TestsOnly construct
# counts only in a test class: in a double that merely offers it, it is a capability, not something done.
$script:PurityRules = @(
  @{ Id = 'Container'; Category = 'Integration'; Construct = 'a container, or a shared container or database fixture'
    Pattern = '\bTestcontainers\b|\b(?:ContainerBuilder|PostgreSqlBuilder|RabbitMqBuilder|ServiceBusBuilder|AzuriteBuilder|MsSqlBuilder|RedisBuilder|DistributedApplicationTestingBuilder)\b|\b(?:Shared\w*Container|PerTestDatabase\w*|DockerExecutable)\b|\.WithImage\s*\(' }
  @{ Id = 'RealHttp'; Category = 'Integration'; Construct = 'an HttpClient with no test handler (a real endpoint)'
    Pattern = '\bnew\s+HttpClient\s*(?:\(\s*\)|\{)' }
  @{ Id = 'StartAsync'; Category = 'Component'; Construct = 'a worker or host StartAsync'
    Pattern = '\.StartAsync\s*\(' }
  @{ Id = 'HostedService'; Category = 'Component'; Construct = 'a type deriving from BackgroundService or IHostedService'
    Pattern = ':\s*(?:[\w.<>]+\s*,\s*)*(?:BackgroundService|IHostedService|IHostedLifecycleService)\b' }
  @{ Id = 'HostBuilder'; Category = 'Component'; Construct = 'a generic or web host builder'
    Pattern = '\bHost\.Create(?:Default|Application|Empty)Builder\s*\(|\bnew\s+HostBuilder\s*\(|\bWebApplication\.Create(?:Builder|SlimBuilder|EmptyBuilder)?\s*\(' }
  @{ Id = 'TestServer'; Category = 'Component'; Construct = 'an in-process test server'
    Pattern = '\bnew\s+TestServer\s*\(|\bUseTestServer\s*\(|\bWebApplicationFactory\s*<|\bGetTest(?:Server|Client)\s*\(' }
  @{ Id = 'ThreadSleep'; Category = 'Component'; Construct = 'Thread.Sleep'
    Pattern = '\bThread\.Sleep\s*\(' }
  @{ Id = 'TaskDelay'; Category = 'Component'; Construct = 'a non-infinite Task.Delay'
    Pattern = '\bTask\.Delay\s*\((?!\s*(?:(?:System\.Threading\.)?Timeout\.Infinite(?:TimeSpan)?\b|-\s*1\s*[,)]))' }
  @{ Id = 'NewThread'; Category = 'Component'; Construct = 'a new Thread'
    Pattern = '\bnew\s+(?:System\.Threading\.)?Thread\s*\(' }
  @{ Id = 'TaskRun'; Category = 'Component'; Construct = 'Task.Run or Task.Factory.StartNew'
    Pattern = '\bTask\.Run\s*\(|\bTask\.Factory\.StartNew\s*\(' }
  @{ Id = 'ThreadPool'; Category = 'Component'; Construct = 'the thread pool'
    Pattern = '\bThreadPool\.\w+' }
  @{ Id = 'Parallel'; Category = 'Component'; Construct = 'Parallel.For, ForEach, ForEachAsync or Invoke'
    Pattern = '\bParallel\.(?:For|ForEach|ForEachAsync|Invoke)\b' }
  @{ Id = 'Timer'; Category = 'Component'; Construct = 'a real timer'
    Pattern = '\bnew\s+(?:System\.Threading\.|System\.Timers\.)?(?:Timer|PeriodicTimer)\s*\(' }
  @{ Id = 'TimeoutWait'; Category = 'Component'; Construct = 'a timeout used as a wait (WaitAsync, Wait, CancelAfter, a timed CancellationTokenSource)'
    Pattern = '\.WaitAsync\s*\(\s*(?:TimeSpan\b|\d|[\w.]*(?:[Tt]imeout|TIMEOUT|[Ww]ait|WAIT|[Ss]afetyNet|[Dd]eadline)[\w.]*\s*[,)])|\.Wait(?:One)?\s*\(\s*(?:TimeSpan\b|\d)|\.CancelAfter\s*\(|\bnew\s+CancellationTokenSource\s*\(\s*[^)\s]' }
  @{ Id = 'CrossThreadSignal'; Category = 'Component'; Construct = 'a test waiting on a signal completed from another thread (RunContinuationsAsynchronously)'
    Pattern = '\bRunContinuationsAsynchronously\b'; TestsOnly = $true }
  @{ Id = 'RealClock'; Category = 'Other'; Construct = 'the real clock (TimeProvider.System)'
    Pattern = '\bTimeProvider\.System\b' }
  @{ Id = 'Stopwatch'; Category = 'Other'; Construct = 'elapsed real time (Stopwatch)'
    Pattern = '\bStopwatch\.(?:StartNew|GetTimestamp|GetElapsedTime)\s*\(|\bnew\s+Stopwatch\s*\(' }
  @{ Id = 'FileSystem'; Category = 'Other'; Construct = 'the real file system'
    Pattern = '(?<![\w.])(?:System\.IO\.)?(?:File|Directory)\.(?!Exists\b)[A-Z]\w*\s*\(|\bPath\.GetTemp(?:Path|FileName)\s*\(|\bnew\s+(?:FileInfo|DirectoryInfo|FileStream|FileSystemWatcher)\s*\(' }
  @{ Id = 'Process'; Category = 'Other'; Construct = 'a child process'
    Pattern = '\bProcess\.Start\s*\(|\bnew\s+(?:System\.Diagnostics\.)?Process(?:StartInfo)?\s*[({]' }
  @{ Id = 'Socket'; Category = 'Other'; Construct = 'a real socket'
    Pattern = '\bnew\s+(?:TcpListener|TcpClient|UdpClient|Socket)\s*\(' }
  @{ Id = 'NetworkClient'; Category = 'Other'; Construct = 'a real RabbitMQ or Postgres connection with no infrastructure behind it, which the code under test dials'
    Pattern = '\bnew\s+(?:ConnectionFactory|NpgsqlConnection|NpgsqlDataSourceBuilder)\s*[({]|\bNpgsqlDataSource\.Create\s*\(' }
) | ForEach-Object {
  [pscustomobject]@{ Id = $_.Id; Category = $_.Category; Construct = $_.Construct; Regex = [regex]::new($_.Pattern); TestsOnly = $_.ContainsKey('TestsOnly') }
}

# The projects under tests/ and samples/ declaring the Unit type, in ordinal order of their directory.
function Get-UnitTestProject {
  param([Parameter(Mandatory)] [string]$Root)
  $projects = [System.Collections.Generic.List[object]]::new()
  foreach ($top in @('tests', 'samples')) {
    $base = Join-Path -Path $Root -ChildPath $top
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Filter '*.csproj') {
      $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
      if ($relative -match '(^|/)(bin|obj)/') { continue }
      $content = [System.IO.File]::ReadAllText($file.FullName)
      if ($content -notmatch '<WhizbangTestType>\s*Unit\s*</WhizbangTestType>') { continue }
      $tags = ''
      if ($content -match '<WhizbangTestTags>([^<]*)</WhizbangTestTags>') { $tags = $Matches[1].Trim() }
      $projects.Add([pscustomobject]@{
          Name      = $file.BaseName
          Directory = $relative.Substring(0, $relative.LastIndexOf([char]'/'))
          FullPath  = $file.DirectoryName
          Tags      = $tags
        })
    }
  }
  $sorted = $projects.ToArray()
  $keys = [string[]]@($sorted | ForEach-Object { $_.Directory })
  [Array]::Sort($keys, $sorted, [System.StringComparer]::Ordinal)
  return $sorted
}

# The text with comments, string and character literals and using directives blanked, keeping every index
# and line break, so a match in the result is code and its line number is the line in the file.
function Remove-CSharpNoise {
  param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text)
  $chars = $Text.ToCharArray()
  foreach ($pattern in @($script:NoisePattern, $script:UsingPattern)) {
    foreach ($m in $pattern.Matches([string]::new($chars))) {
      [Array]::Clear($chars, $m.Index, $m.Length)
      $at = $m.Value.IndexOf([char]10)
      while ($at -ge 0) {
        $chars[$m.Index + $at] = [char]10
        $at = $m.Value.IndexOf([char]10, $at + 1)
      }
    }
  }
  return [string]::new($chars).Replace([char]0, [char]32)
}

# The 1-based line an index falls on.
function Get-LineNumber {
  param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text, [Parameter(Mandatory)] [int]$Index)
  return $Text.Substring(0, $Index).Split([char]10).Length
}

# The top-level types of a blanked file: Name, Namespace, Partial, Line, and the segment [Start, End) they own.
function Get-CSharpTopLevelType {
  param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text)
  $stack = [System.Collections.Generic.List[string]]::new()
  $namespaceNames = [System.Collections.Generic.List[string]]::new()
  $namespaces = 0
  $segmentStart = 0
  $current = $null
  foreach ($m in $script:StructurePattern.Matches($Text)) {
    $value = $m.Value
    if ($value -ceq '}') {
      if ($stack.Count -eq 0) { continue }
      if ($stack[$stack.Count - 1] -ceq 'namespace') { $namespaces--; $namespaceNames.RemoveAt($namespaceNames.Count - 1) }
      $stack.RemoveAt($stack.Count - 1)
      if ($null -ne $current -and $stack.Count -eq $current.Depth) {
        $current.End = $m.Index + 1
        $current
        $current = $null
        $segmentStart = $m.Index + 1
      }
      continue
    }
    $opens = $value.EndsWith('{', [System.StringComparison]::Ordinal)
    if ($m.Groups[1].Success) {
      $segmentStart = $m.Index + $m.Length
      if ($opens) { $stack.Add('namespace'); $namespaces++ }
      # A file-scoped namespace is the first name for the rest of the file; a block one nests.
      $namespaceNames.Add($m.Groups[1].Value)
      continue
    }
    if (-not $opens) { continue }
    $isType = $m.Groups[2].Success
    if ($isType -and $stack.Count -eq $namespaces) {
      $current = [pscustomobject]@{
        Name      = $m.Groups[2].Value
        Namespace = $namespaceNames -join '.'
        Partial   = $Text.Substring($segmentStart, $m.Index - $segmentStart) -cmatch '\bpartial\s*$'
        Line      = Get-LineNumber -Text $Text -Index $m.Groups[2].Index
        Start     = $segmentStart
        End       = $Text.Length
        Depth     = $stack.Count
      }
    }
    $stack.Add($(if ($isType) { 'type' } else { 'block' }))
  }
  if ($null -ne $current) { $current }
}

# Every non-unit construct in a blanked text, rule by rule, each with its category and line.
function Find-PurityConstruct {
  param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text)
  foreach ($rule in $script:PurityRules) {
    foreach ($m in $rule.Regex.Matches($Text)) {
      [pscustomobject]@{ Rule = $rule.Id; Category = $rule.Category; TestsOnly = $rule.TestsOnly; Index = $m.Index; Line = Get-LineNumber -Text $Text -Index $m.Index }
    }
  }
}

# The worst of a set of categories: Integration > Component > Other > Unit.
function Get-PurityCategory {
  param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Categories)
  $worst = 'Unit'
  foreach ($category in $Categories) {
    if ($script:CategoryRank[$category] -gt $script:CategoryRank[$worst]) { $worst = $category }
  }
  return $worst
}

# The top-level types of one C# file, each with its own constructs and category.
function Get-TestFileType {
  param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [string]$RelativePath,
    [Parameter(Mandatory)] [string]$Project
  )
  $text = Remove-CSharpNoise -Text ([System.IO.File]::ReadAllText($Path))
  $findings = @(Find-PurityConstruct -Text $text)
  foreach ($type in @(Get-CSharpTopLevelType -Text $text)) {
    $segment = $text.Substring($type.Start, $type.End - $type.Start)
    $tests = $script:TestAttributePattern.Matches($segment).Count
    $own = @($findings | Where-Object { $_.Index -ge $type.Start -and $_.Index -lt $type.End -and ($tests -gt 0 -or -not $_.TestsOnly) })
    # The type names this segment declares, itself and its nested types included.
    $declared = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($m in $script:StructurePattern.Matches($segment)) {
      if ($m.Groups[2].Success) { [void]$declared.Add($m.Groups[2].Value) }
    }
    [pscustomobject]@{
      Project     = $Project
      File        = $RelativePath
      Class       = $type.Name
      Namespace   = $type.Namespace
      Partial     = $type.Partial
      Line        = $type.Line
      Tests       = $tests
      Findings    = $own
      Category    = Get-PurityCategory -Categories @($own | ForEach-Object { $_.Category })
      Declared    = $declared
      Identifiers = [System.Collections.Generic.HashSet[string]]::new([string[]]($segment -split '\W+'), [System.StringComparer]::Ordinal)
      References  = [string[]]@()
      Via         = [string[]]@()
    }
  }
}

# Carries helper categories through the names one project's types use, to a fixed point, and records Via.
function Resolve-TypeReference {
  param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Types)
  $declared = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
  foreach ($type in $Types) { $declared[$type.Class] = 1 + $(if ($declared.ContainsKey($type.Class)) { $declared[$type.Class] } else { 0 }) }
  $unique = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
  foreach ($type in $Types) { if ($declared[$type.Class] -eq 1 -and $type.Tests -eq 0) { $unique[$type.Class] = $type } }
  $names = [System.Collections.Generic.HashSet[string]]::new([string[]]@($unique.Keys), [System.StringComparer]::Ordinal)
  foreach ($type in $Types) {
    $references = [System.Collections.Generic.HashSet[string]]::new($type.Identifiers, [System.StringComparer]::Ordinal)
    $references.IntersectWith($names)
    $references.ExceptWith($type.Declared)
    $type.References = [string[]]@($references)
  }
  # The parts of one partial class (same namespace and name) are one class, and move together.
  $parts = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::Ordinal)
  foreach ($type in $Types) {
    if (-not $type.Partial) { continue }
    $key = "$($type.Namespace).$($type.Class)"
    if (-not $parts.ContainsKey($key)) { $parts[$key] = [System.Collections.Generic.List[object]]::new() }
    $parts[$key].Add($type)
  }
  $changed = $true
  while ($changed) {
    $changed = $false
    foreach ($type in $Types) {
      foreach ($name in $type.References) {
        $used = $unique[$name].Category
        if ($script:CategoryRank[$used] -gt $script:CategoryRank[$type.Category]) { $type.Category = $used; $changed = $true }
      }
    }
    foreach ($group in $parts.Values) {
      $whole = Get-PurityCategory -Categories @($group | ForEach-Object { $_.Category })
      foreach ($part in $group) {
        if ($part.Category -ne $whole) { $part.Category = $whole; $changed = $true }
      }
    }
  }
  $splitClasses = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  foreach ($key in $parts.Keys) {
    if ($parts[$key].Count -ge 2 -and $parts[$key][0].Category -ne 'Unit') { [void]$splitClasses.Add($key) }
  }
  foreach ($type in $Types) {
    $via = [string[]]@(foreach ($name in $type.References) {
        $used = $unique[$name].Category
        if ($used -ne 'Unit') { "$name ($used)" }
      }
      if ($type.Partial -and $splitClasses.Contains("$($type.Namespace).$($type.Class)")) { "partial $($type.Class) ($($type.Category))" })
    [Array]::Sort($via, [System.StringComparer]::Ordinal)
    $type.Via = $via
  }
}

# Every top-level type of every Unit-type project, classified: @{ Projects; Types }.
function Get-TestPurityInventory {
  param([Parameter(Mandatory)] [string]$Root)
  $projects = @(Get-UnitTestProject -Root $Root)
  $all = [System.Collections.Generic.List[object]]::new()
  foreach ($project in $projects) {
    $paths = [string[]]@(Get-ChildItem -LiteralPath $project.FullPath -Recurse -File -Filter '*.cs' |
        Where-Object { [System.IO.Path]::GetRelativePath($project.FullPath, $_.FullName).Replace('\', '/') -notmatch '(^|/)(bin|obj)/' } |
        ForEach-Object { $_.FullName })
    [Array]::Sort($paths, [System.StringComparer]::Ordinal)
    $types = [System.Collections.Generic.List[object]]::new()
    foreach ($path in $paths) {
      $relative = [System.IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
      foreach ($type in @(Get-TestFileType -Path $path -RelativePath $relative -Project $project.Name)) { $types.Add($type) }
    }
    Resolve-TypeReference -Types $types.ToArray()
    $all.AddRange($types)
  }
  return [pscustomobject]@{ Projects = $projects; Types = $all.ToArray() }
}

# A type's finding lines grouped by rule, in rule order.
function Get-FindingLine {
  param([Parameter(Mandatory)] $Type)
  $byRule = @{}
  foreach ($finding in $Type.Findings) {
    if (-not $byRule.ContainsKey($finding.Rule)) { $byRule[$finding.Rule] = [System.Collections.Generic.List[int]]::new() }
    $byRule[$finding.Rule].Add($finding.Line)
  }
  $ordered = [ordered]@{}
  foreach ($rule in $script:PurityRules) {
    if ($byRule.ContainsKey($rule.Id)) { $ordered[$rule.Id] = $byRule[$rule.Id].ToArray() }
  }
  return $ordered
}

# The purity report: a summary line, then each project with its non-unit types and why.
function Format-TestPurityReport {
  param([Parameter(Mandatory)] $Inventory)
  $types = @($Inventory.Types)
  $projects = @($Inventory.Projects)
  $tests = @($types | Where-Object { $_.Tests -gt 0 }).Count
  $violations = @($types | Where-Object { $_.Category -ne 'Unit' }).Count
  "Test purity: $($projects.Count) Unit-type projects, $($types.Count) types ($tests test classes); $violations not unit-pure."
  $categoryOf = @{}
  foreach ($rule in $script:PurityRules) { $categoryOf[$rule.Id] = $rule.Category }
  foreach ($project in $projects) {
    $own = @($types | Where-Object { $_.Project -ceq $project.Name })
    $bad = @($own | Where-Object { $_.Category -ne 'Unit' })
    if ($bad.Count -eq 0) {
      "$($project.Name) ($($project.Directory)): clean, $($own.Count) types"
      continue
    }
    "$($project.Name) ($($project.Directory)): $($bad.Count) of $($own.Count) types not unit-pure"
    foreach ($type in $bad) {
      "  $($type.Class) [$($type.Category)] $($type.File):$($type.Line) ($($type.Tests) tests)"
      $lines = Get-FindingLine -Type $type
      foreach ($rule in $lines.Keys) { "    $rule ($($categoryOf[$rule])): lines $($lines[$rule] -join ', ')" }
      foreach ($via in $type.Via) { "    via $via" }
    }
  }
}

# The report; throws when the scan saw nothing, or when enforced and any type is not unit-pure.
function Assert-TestPurity {
  param([Parameter(Mandatory)] $Inventory, [Parameter()] [switch]$Enforced)
  if (@($Inventory.Projects).Count -eq 0) {
    throw 'The purity scan found no Unit-type test project, so it checked nothing. Check the root and the <WhizbangTestType> declarations.'
  }
  if (@($Inventory.Types | Where-Object { $_.Tests -gt 0 }).Count -eq 0) {
    throw 'The purity scan found no test class in the Unit-type projects, so it checked nothing.'
  }
  $report = @(Format-TestPurityReport -Inventory $Inventory)
  $violations = @($Inventory.Types | Where-Object { $_.Category -ne 'Unit' })
  if ($Enforced -and $violations.Count -gt 0) {
    throw "$($violations.Count) type(s) in Unit-type projects are not unit tests; move each to a project of its type (#1264):`n$($report -join "`n")"
  }
  return $report
}

# One inventory row. Constructs are rule ids in rule order; Reasons give each rule's lines in the file.
function ConvertTo-TestPurityRow {
  param([Parameter(Mandatory)] $Type)
  $lines = Get-FindingLine -Type $Type
  $reasons = @(foreach ($rule in $lines.Keys) { "$rule $(@($lines[$rule] | ForEach-Object { "L$_" }) -join ',')" })
  $note = ''
  if ($Type.Project -ceq 'Whizbang.Core.Tests' -and $Type.Category -ne 'Unit' -and $Type.File.StartsWith('tests/Whizbang.Core.Tests/Workers/', [System.StringComparison]::Ordinal)) {
    $note = 'phase 2 waits for #1259'
  }
  return [pscustomobject][ordered]@{
    Project    = $Type.Project
    File       = $Type.File
    Class      = $Type.Class
    Line       = $Type.Line
    Kind       = $(if ($Type.Tests -gt 0) { 'Test' } else { 'Helper' })
    Tests      = $Type.Tests
    Category   = $Type.Category
    Constructs = @($lines.Keys) -join ' '
    Reasons    = $reasons -join '; '
    Via        = @($Type.Via) -join '; '
    Note       = $note
  }
}

# The generated part of the inventory markdown: counts per project, then the non-unit types per project.
function ConvertTo-TestPurityMarkdown {
  param([Parameter(Mandatory)] $Inventory)
  $types = @($Inventory.Types)
  $out = [System.Collections.Generic.List[string]]::new()
  $out.Add('## Counts per project')
  $out.Add('')
  $out.Add('Test classes (top-level types with at least one `[Test]`) by category. A non-unit helper is a top-level type with no test that uses a non-unit construct; it moves with the classes that use it.')
  $out.Add('')
  $out.Add('| Project | Unit | Component | Integration | Other | Total | Non-unit helpers |')
  $out.Add('|---|---:|---:|---:|---:|---:|---:|')
  $totals = [ordered]@{ Unit = 0; Component = 0; Integration = 0; Other = 0; Total = 0; Helpers = 0 }
  foreach ($project in @($Inventory.Projects)) {
    $own = @($types | Where-Object { $_.Project -ceq $project.Name })
    $tests = @($own | Where-Object { $_.Tests -gt 0 })
    $cells = foreach ($category in $script:CategoryOrder) {
      $count = @($tests | Where-Object { $_.Category -eq $category }).Count
      $totals[$category] += $count
      $count
    }
    $helpers = @($own | Where-Object { $_.Tests -eq 0 -and $_.Category -ne 'Unit' }).Count
    $totals.Total += $tests.Count
    $totals.Helpers += $helpers
    $out.Add("| $($project.Name) | $($cells -join ' | ') | $($tests.Count) | $helpers |")
  }
  $out.Add("| **Total** | $(@($totals.Values) -join ' | ') |")
  $out.Add('')
  $out.Add('## Worklist: non-unit types by project')
  $out.Add('')
  $out.Add('Every top-level type that is not unit-pure, in file order. Tests = 0 marks a helper. The CSV has the line of every construct.')
  foreach ($project in @($Inventory.Projects)) {
    $own = @($types | Where-Object { $_.Project -ceq $project.Name })
    $bad = @($own | Where-Object { $_.Category -ne 'Unit' })
    $out.Add('')
    $out.Add("### $($project.Name)")
    $out.Add('')
    if ($bad.Count -eq 0) {
      $out.Add("All $($own.Count) types are unit-pure.")
      continue
    }
    $out.Add("$($bad.Count) of $($own.Count) types are not unit-pure.")
    $out.Add('')
    $out.Add('| Class | File | Tests | Category | Constructs | Via | Note |')
    $out.Add('|---|---|---:|---|---|---|---|')
    foreach ($type in $bad) {
      $row = ConvertTo-TestPurityRow -Type $type
      $out.Add("| $($row.Class) | ``$($row.File)`` | $($row.Tests) | $($row.Category) | $($row.Constructs.Replace(' ', ', ')) | $($row.Via) | $($row.Note) |")
    }
  }
  return ($out -join "`n") + "`n"
}

# Writes the CSV, and the markdown's generated part below the marker, keeping the text above it.
function Export-TestPurityInventory {
  param([Parameter(Mandatory)] $Inventory, [Parameter(Mandatory)] [string]$CsvPath, [Parameter(Mandatory)] [string]$MarkdownPath)
  foreach ($path in @($CsvPath, $MarkdownPath)) { New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null }
  @($Inventory.Types | ForEach-Object { ConvertTo-TestPurityRow -Type $_ }) |
    Export-Csv -LiteralPath $CsvPath -NoTypeInformation -UseQuotes Always -Encoding utf8
  $head = "# Test separation inventory`n`n"
  if (Test-Path -LiteralPath $MarkdownPath) {
    $existing = [System.IO.File]::ReadAllText($MarkdownPath)
    $at = $existing.IndexOf($script:GeneratedMarker, [System.StringComparison]::Ordinal)
    $head = if ($at -ge 0) { $existing.Substring(0, $at) } else { $existing.TrimEnd() + "`n`n" }
  }
  $markdown = $head + $script:GeneratedMarker + "`n`n" + (ConvertTo-TestPurityMarkdown -Inventory $Inventory)
  [System.IO.File]::WriteAllText($MarkdownPath, $markdown)
}

# Dot-sourcing for tests loads the functions without scanning anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$result = Get-TestPurityInventory -Root $resolvedRoot
if ($Inventory) {
  # The sanity checks only: an inventory of nothing must not be written.
  $null = Assert-TestPurity -Inventory $result
  $csv = $(if ($CsvPath) { $CsvPath } else { Join-Path -Path $resolvedRoot -ChildPath 'plans/test-separation-inventory.csv' })
  $md = $(if ($MarkdownPath) { $MarkdownPath } else { Join-Path -Path $resolvedRoot -ChildPath 'plans/test-separation-inventory.md' })
  $csv = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($csv)
  $md = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($md)
  Export-TestPurityInventory -Inventory $result -CsvPath $csv -MarkdownPath $md
  Write-Output "Wrote $csv and $md"
  return
}
Assert-TestPurity -Inventory $result -Enforced:$script:PurityEnforced
