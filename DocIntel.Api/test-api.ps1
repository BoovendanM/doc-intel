# Curl tests for DocIntel.Api
# Usage (from the DocIntel.Api folder, with the API running via `dotnet run --launch-profile http`):
#   .\test-api.ps1
#   .\test-api.ps1 -BaseUrl https://localhost:7236
#
# Uses curl.exe explicitly, because `curl` in Windows PowerShell 5.1 is an alias for Invoke-WebRequest.
# JSON bodies are piped through stdin (--data-binary '@-') so quoting works in both PowerShell 5.1 and 7+.

param(
    [string]$BaseUrl = "http://localhost:5151",
    [string]$File = (Join-Path $PSScriptRoot "sample.txt")
)

# -k skips certificate validation for the local dev HTTPS certificate.
$common = @("-s", "-k", "-w", "`nHTTP %{http_code}`n")

function Section($title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------- health
Section "Health: liveness (expect 200)"
curl.exe @common "$BaseUrl/health/live"

Section "Health: readiness - MongoDB, Ollama, vector store (expect 200; 503 if a dependency is down)"
curl.exe @common "$BaseUrl/health"

# ---------------------------------------------------------------- ingest
Section "Ingest: valid text file (expect 200)"
curl.exe @common -X POST "$BaseUrl/api/ingest" -F "file=@$File;type=text/plain"

Section "Ingest: unsupported extension (expect 400)"
$badFile = Join-Path $env:TEMP "docintel-test.exe"
Set-Content -Path $badFile -Value "not a document"
curl.exe @common -X POST "$BaseUrl/api/ingest" -F "file=@$badFile"
Remove-Item $badFile -ErrorAction SilentlyContinue

Section "Ingest: empty file (expect 400)"
$emptyFile = Join-Path $env:TEMP "docintel-empty.txt"
New-Item -Path $emptyFile -ItemType File -Force | Out-Null
curl.exe @common -X POST "$BaseUrl/api/ingest" -F "file=@$emptyFile"
Remove-Item $emptyFile -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- ask
Section "Ask: answer is in the document (expect 200 + sources)"
'{"question":"How much is the home office allowance and how do I claim it?"}' |
    curl.exe @common -X POST "$BaseUrl/api/ask" -H "Content-Type: application/json" --data-binary "@-"

Section "Ask: another in-document question (expect 200)"
'{"question":"What are the core hours for remote employees?"}' |
    curl.exe @common -X POST "$BaseUrl/api/ask" -H "Content-Type: application/json" --data-binary "@-"

Section "Ask: answer NOT in the document (expect 'I don't know')"
'{"question":"What is the parental leave policy?"}' |
    curl.exe @common -X POST "$BaseUrl/api/ask" -H "Content-Type: application/json" --data-binary "@-"

Section "Ask: empty question (expect 400)"
'{"question":""}' |
    curl.exe @common -X POST "$BaseUrl/api/ask" -H "Content-Type: application/json" --data-binary "@-"

