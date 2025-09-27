# startup Dexter (gpt-5 hackathon)

Minimal ASP.NET Core API for parsing PowerPoint decks into structured JSON and rendering slide previews as PDF for the secure knowledge base prototype.

## Prerequisites
- .NET 9 SDK (`dotnet --version`)
- Python 3.10+ (`python3 --version`) for the Flask instruction parser
- LibreOffice (`soffice`) available on `PATH` or via `SOFFICE_PATH` if you plan to use `/api/render`

## Run the API
```bash
dotnet restore apps/api-dotnet/Dexter.WebApi.csproj
dotnet run --project apps/api-dotnet/Dexter.WebApi.csproj
```

The default launch profile exposes `http://localhost:5100`.

## Configuration
`apps/api-dotnet/Properties/launchSettings.json` seeds local environment variables:
- `applicationUrl`: `http://localhost:5100`
- `SEMANTIC_URL`: `http://localhost:8000` (placeholder for future semantic services)
- `SOFFICE_PATH`: `/Applications/LibreOffice.app/Contents/MacOS/soffice`

Override any value before running if your setup differs:

```bash
export SOFFICE_PATH=/usr/local/bin/soffice
dotnet run --project apps/api-dotnet/Dexter.WebApi.csproj
```

## Endpoints
- `POST /api/extract` — accepts multipart field `file` with a `.pptx`; optional `instructions` text is forwarded to Flask for rule inference before local redaction.
- `POST /api/render` — accepts multipart field `file`, converts the deck to PDF for inline viewing; requires LibreOffice.

### Instruction Parser (Flask)
```bash
cd apps/python-semantic
python3 -m venv .venv
source .venv/bin/activate   # .venv\Scripts\activate on Windows
pip install -r requirements.txt
FLASK_APP=app.py flask run --host 0.0.0.0 --port 8000 --debug
```

### Quick test
```bash
curl -F "file=@/absolute/path/to/deck.pptx" http://localhost:5100/api/extract
curl -F "file=@/absolute/path/to/deck.pptx" -F "instructions=Redact client names and revenue" http://localhost:5100/api/extract
```

Example response:

```json
{
  "file": "deck.pptx",
  "slideCount": 17,
  "slides": [...]
}
```

## Repo layout
```
apps/
└─ api-dotnet/
   ├─ Dexter.WebApi.csproj
   ├─ Program.cs
   └─ Properties/
      └─ launchSettings.json
```

## Troubleshooting
- LibreOffice missing → `/api/render` returns 501; install LibreOffice or set `SOFFICE_PATH`.
- Port collision → update `applicationUrl` in `launchSettings.json`.
- CORS is wide open for local prototyping; tighten before shipping.
