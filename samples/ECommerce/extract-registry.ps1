param([string]$InputFile, [string]$OutputFile)

$content = Get-Content $InputFile -Raw

if ($content -match '(?s)internal const string Json = @"(.+?)";') {
    $json = $matches[1] -replace '""', '"'
    $json | Out-File $OutputFile -Encoding UTF8 -NoNewline
    Write-Host "Extracted JSON to $OutputFile"
} else {
    Write-Error "Could not find JSON string in $InputFile"
    exit 1
}
