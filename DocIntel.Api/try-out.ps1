# Health (full report, then liveness)
curl.exe http://localhost:5151/health
curl.exe http://localhost:5151/health/live

# Ingest
curl.exe -X POST http://localhost:5151/api/ingest -F "file=@sample.txt;type=text/plain"

# List stored documents in MongoDB
curl.exe http://localhost:5151/api/documents

# Ask (the JSON is piped in, so the quotes survive in PowerShell 5.1 and 7)
'{"question":"How much is the home office allowance?"}' | curl.exe -X POST http://localhost:5151/api/ask -H "Content-Type: application/json" --data-binary "@-"