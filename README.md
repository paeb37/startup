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
- `SUPABASE_URL`: base URL of your Supabase instance (e.g. `https://abc.supabase.co`)
- `SUPABASE_SERVICE_ROLE_KEY`: service role API key used for inserts
- `OPENAI_API_KEY`: key used for slide embeddings (optional but recommended)
- `OPENAI_EMBEDDING_MODEL`: embedding model name (defaults to `text-embedding-3-small`)
- `SUPABASE_DECKS_TABLE`: override for the decks table name (defaults to `decks`)
- `SUPABASE_SLIDES_TABLE`: override for the slides table name (defaults to `slides`)

Override any value before running if your setup differs:

```bash
export SOFFICE_PATH=/usr/local/bin/soffice
dotnet run --project apps/api-dotnet/Dexter.WebApi.csproj
```

## Endpoints
- `POST /api/upload` — accepts multipart field `file` (optional `instructions`); stores the artifacts under `apps/storage/<name>/` and returns deck JSON alongside a base64-encoded PDF preview.
- `POST /api/render` — accepts multipart field `file`, converts the deck to PDF for inline viewing; requires LibreOffice. (Primarily kept for tooling—`/api/upload` already returns the PDF.)

### Supabase schema

Enable the `vector` extension once, then create the tables expected by `/api/upload`:

```sql
create extension if not exists vector;

create table if not exists decks (
  id uuid primary key,
  deck_name text not null,
  original_filename text not null,
  pptx_path text not null,
  json_path text not null,
  pdf_path text not null,
  pdf_url text not null,
  instructions text,
  slide_count integer not null,
  created_at timestamptz not null default timezone('utc', now()),
  updated_at timestamptz not null default timezone('utc', now())
);

create table if not exists slides (
  id uuid primary key,
  deck_id uuid references decks(id) on delete cascade,
  slide_no integer not null,
  content text,
  embedding vector(1536),
  created_at timestamptz not null default timezone('utc', now()),
  updated_at timestamptz not null default timezone('utc', now())
);

create index if not exists slides_deck_id_slide_no_idx on slides(deck_id, slide_no);
create index if not exists slides_embedding_idx on slides using ivfflat (embedding vector_l2_ops) with (lists = 100);
```

Grant your service role access to both tables, or configure Supabase Row Level Security policies as needed for your workflow.

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
curl -H "Accept: application/json" \
     -F "file=@/absolute/path/to/deck.pptx" \
     http://localhost:5100/api/upload

curl -H "Accept: application/json" \
     -F "file=@/absolute/path/to/deck.pptx" \
     -F "instructions=Redact client names and revenue" \
     http://localhost:5100/api/upload
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
