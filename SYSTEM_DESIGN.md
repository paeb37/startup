# Dexter - System Design

## Overview

**Dexter** is a secure knowledge base system for redacting sensitive PowerPoint presentations with AI-powered semantic search. Users can upload PPTX files, define redaction rules in natural language, preview changes, and search across decks using vector embeddings.

---

## Architecture

### System Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                 Frontend (React + Vite)                      │
│                      Port: 5173                              │
└────────────────────────┬────────────────────────────────────┘
                         │
        ┌────────────────┼────────────────┐
        │                │                │
        ▼                ▼                ▼
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│   .NET API   │  │ Python Flask │  │   Supabase   │
│  Port: 5100  │  │  Port: 8000  │  │   (Cloud)    │
│              │  │              │  │              │
│ - Extract    │  │ - Parse      │  │ - PostgreSQL │
│ - Redact     │  │ - Embed      │  │ - pgvector   │
│ - Convert    │  │ - Q&A        │  │ - Storage    │
└──────┬───────┘  └──────┬───────┘  └──────────────┘
       │                 │
       ▼                 ▼
┌──────────────┐  ┌──────────────┐
│ LibreOffice  │  │   OpenAI     │
│  Converter   │  │     API      │
└──────────────┘  └──────────────┘
```

---

## Core Components

### 1. .NET API (`apps/api-dotnet/`)

**Purpose**: Document processing, extraction, and redaction  
**Port**: 5100

#### Key Services

**`DeckExtractor`**
- Parses PPTX using OpenXML
- Extracts slides, textboxes, tables, images
- Generates unique element keys: `{slidePath}#{type}#{id}`

**`DeckRedactionService`**
- Applies rule actions to PPTX
- Modifies OpenXML elements in-memory
- Preserves formatting while replacing text

**`DeckWorkflowService`**
- Orchestrates upload/preview/redaction workflows
- Manages caching and file streaming
- Coordinates between services

**`SupabaseClient`**
- Uploads artifacts to storage
- Inserts deck/slide/embedding records
- Fetches rule actions for redaction

**`OpenAiClient`**
- Generates text embeddings (text-embedding-3-small)
- Creates image captions (gpt-4o-mini vision)
- Summarizes table content

**`ConverterClient`**
- Calls LibreOffice Docker container
- Fast PPTX→PDF conversion (~0.5s)

#### Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `POST` | `/api/upload` | Upload PPTX, extract & generate embeddings |
| `GET` | `/api/decks` | List decks with pagination |
| `POST` | `/api/decks/{id}/preview` | Preview with rules applied (in-memory) |
| `POST` | `/api/decks/{id}/redact` | Apply rules, generate redacted artifacts |
| `GET` | `/api/decks/{id}/download` | Download redacted PPTX/PDF |
| `GET` | `/api/decks/{id}/slides/{no}` | Get slide thumbnail PNG |

---

### 2. Python Flask Service (`apps/python-semantic/`)

**Purpose**: AI orchestration for rules and search  
**Port**: 8000

#### Key Functions

**Redaction Rule Processing** (`POST /redact`)
1. Parse instructions (heuristic + LLM)
2. Generate rule actions by iterating deck elements
3. Apply text transformations based on scope
4. Persist to `rules` and `rule_actions` tables

**Semantic Search & Q&A** (`POST /api/chat`)
1. Embed query using OpenAI
2. Vector search in Supabase (cosine similarity)
3. Build context from matching slides
4. Generate answer with citations using LLM

#### Core Logic

**`generate_rule_actions()`**
- Iterates through deck slides and textbox elements
- Applies actions based on scope (slide range/list)
- Returns list of changes: `{slide_no, element_key, original_text, new_text}`

**`apply_text_action()`**
- **Keyword mode**: Case-insensitive token replacement
- **Regex mode**: Pattern-based substitution
- Returns: (new_text, changed_flag)

**`answer_question()`**
1. Embed query → vector
2. Match slides (top 5 by similarity)
3. Fetch deck JSONs, extract slide text
4. LLM generates answer with `[Deck Name Slide X]` citations

---

### 3. React Frontend (`apps/web-frontend/`)

**Purpose**: UI for upload, redaction, and preview  
**Port**: 5173

#### Views

**Home** - Landing dashboard  
**Library** - Browse uploaded decks  
**Upload** - File upload with drag-and-drop  
**Builder** - Main workspace:
- PDF slide viewer with thumbnails
- Rule management panel (add/view rules)
- Preview toggle (original vs redacted)

#### Key Components

**`AddRulePanel`** - Natural language instruction input  
**`ActiveRulesPanel`** - Displays saved rules via `useSupabaseRules()` hook  
**`PreviewToggleButton`** - Switches between original/redacted previews  
**`MainSlideViewer`** - PDF.js-based slide renderer  
**`ThumbRail`** - Thumbnail filmstrip navigator

---

## Data Flows

### Flow 1: Upload & Processing

```
User uploads PPTX
  ↓
.NET /api/upload
  ↓
DeckExtractor parses OpenXML
  ↓
SupabaseClient:
  - Generate slide images (LibreOffice)
  - Create embeddings (OpenAI)
  - Upload to Supabase Storage
  - Insert deck + slides to DB
  ↓
Return deck JSON + PDF base64
```

### Flow 2: Rule Creation

```
User enters: "Redact client names"
  ↓
POST /redact to Python service
  ↓
LLM parses → structured action JSON
  ↓
generate_rule_actions():
  - Match text in textboxes
  - Create rule_action records
  ↓
insert_rule() → rules table
insert_rule_actions() → rule_actions table
  ↓
Return {ruleId, actionCount}
```

### Flow 3: Preview/Redaction

```
User toggles preview
  ↓
POST /api/decks/{id}/preview
  ↓
Fetch rule_actions from Supabase
  ↓
DeckRedactionService:
  - Load original PPTX (memory)
  - Apply text replacements
  ↓
Convert to PDF (LibreOffice)
  ↓
Return base64 PDF
```

### Flow 4: Semantic Q&A

```
User asks: "What are Q4 revenue projections?"
  ↓
POST /api/chat
  ↓
Embed query (OpenAI)
  ↓
Vector search (Supabase RPC: match_slides)
  ↓
Build context from matching slides
  ↓
LLM generates answer with citations
  ↓
Return {reply, sources}
```

---

## Database Schema

### Tables

**`decks`**
```sql
CREATE TABLE decks (
  id UUID PRIMARY KEY,
  deck_name TEXT NOT NULL,
  pptx_path TEXT NOT NULL,
  redacted_pptx_path TEXT,
  redacted_pdf_path TEXT,
  redacted_json_path TEXT,
  slide_count INTEGER NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);
```

**`slides`**
```sql
CREATE TABLE slides (
  id UUID PRIMARY KEY,
  deck_id UUID REFERENCES decks(id) ON DELETE CASCADE,
  slide_no INTEGER NOT NULL,
  embedding VECTOR(1536),
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX slides_embedding_idx ON slides 
  USING ivfflat (embedding vector_l2_ops) WITH (lists = 100);
```

**`rules`**
```sql
CREATE TABLE rules (
  id UUID PRIMARY KEY,
  deck_id UUID REFERENCES decks(id) ON DELETE CASCADE,
  title TEXT NOT NULL,              -- LLM-generated
  user_query TEXT NOT NULL,         -- Original instruction
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);
```

**`rule_actions`**
```sql
CREATE TABLE rule_actions (
  id UUID PRIMARY KEY,
  rule_id UUID REFERENCES rules(id) ON DELETE CASCADE,
  deck_id UUID REFERENCES decks(id) ON DELETE CASCADE,
  slide_no INTEGER NOT NULL,
  element_key TEXT NOT NULL,        -- e.g., "ppt/slides/slide3.xml#textbox#5"
  bbox JSONB,                       -- {x, y, width, height}
  original_text TEXT NOT NULL,
  new_text TEXT NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX rule_actions_deck_id_idx ON rule_actions (deck_id);
```

### RPC Function

**`match_slides`** - Vector similarity search
```sql
CREATE FUNCTION match_slides(
  query_embedding VECTOR(1536),
  match_count INT DEFAULT 5,
  match_threshold FLOAT DEFAULT 0.2
)
RETURNS TABLE (id UUID, deck_id UUID, slide_no INTEGER, similarity FLOAT)
AS $$
  SELECT
    s.id,
    s.deck_id,
    s.slide_no,
    1 - (s.embedding <=> query_embedding) AS similarity
  FROM slides s
  WHERE s.embedding IS NOT NULL
    AND 1 - (s.embedding <=> query_embedding) >= match_threshold
  ORDER BY s.embedding <=> query_embedding
  LIMIT match_count;
$$;
```

---

## API Contracts

### .NET Endpoints

**POST /api/upload**
```json
// Request: multipart/form-data
// - file: PPTX binary
// - instructions: string (optional)

// Response:
{
  "id": "uuid",
  "file": "deck.pptx",
  "slideCount": 17,
  "slides": [
    {
      "index": 0,
      "elements": [
        {
          "type": "textbox",
          "key": "ppt/slides/slide1.xml#textbox#3",
          "bbox": { "x": 685800, "y": 457200, "width": 7772400, "height": 1325563 },
          "paragraphs": [{ "text": "Title" }]
        }
      ]
    }
  ],
  "pdf": "base64..."
}
```

**POST /api/decks/{id}/preview**
```json
// Request:
{
  "ruleIds": ["uuid1", "uuid2"]
}

// Response:
{
  "pdf": "base64...",
  "appliedActionCount": 47
}
```

### Python Endpoints

**POST /redact**
```json
// Request:
{
  "deckId": "uuid",
  "instructions": "Redact client names and revenue",
  "deck": { /* deck JSON */ }
}

// Response:
{
  "deckId": "uuid",
  "ruleId": "uuid",
  "actionCount": 47,
  "meta": { "source": "llm", "model": "gpt-4.1-mini" }
}
```

**POST /api/chat**
```json
// Request:
{
  "message": "What are the revenue projections?"
}

// Response:
{
  "reply": "Revenue is $45M [Deck Slide 7]...",
  "sources": [
    {
      "slideId": "uuid",
      "deckId": "uuid",
      "deckName": "Q4 Forecast",
      "slideNumber": 7,
      "similarity": 0.87,
      "thumbnailUrl": "http://localhost:5100/api/decks/{id}/slides/7"
    }
  ]
}
```

---

## Technologies

### Backend
- **ASP.NET Core 9**: Web framework, OpenXML processing
- **Python Flask**: AI orchestration
- **DocumentFormat.OpenXml**: PPTX parsing/manipulation
- **Docnet.Core**: PDF rendering (PDFium wrapper)
- **OpenAI SDK**: Embeddings, vision, chat

### Frontend
- **React 18 + TypeScript**: UI framework
- **Vite**: Build tool
- **react-pdf**: PDF.js wrapper for slide rendering

### Infrastructure
- **Supabase**: PostgreSQL + pgvector + Storage
- **OpenAI API**: text-embedding-3-small, gpt-4o-mini, gpt-4.1-mini
- **LibreOffice**: Document conversion (Docker container)

---

## Configuration

### Environment Variables

**Core Services**
```bash
# Supabase
SUPABASE_URL=https://your-project.supabase.co
SUPABASE_SERVICE_ROLE_KEY=eyJhbG...
SUPABASE_STORAGE_BUCKET=decks
SUPABASE_DECKS_TABLE=decks
SUPABASE_SLIDES_TABLE=slides
SUPABASE_RULES_TABLE=rules
SUPABASE_RULE_ACTIONS_TABLE=rule_actions

# OpenAI
OPENAI_API_KEY=sk-...
OPENAI_EMBEDDING_MODEL=text-embedding-3-small
OPENAI_VISION_MODEL=gpt-4o-mini
OPENAI_MODEL=gpt-4.1-mini

# Services
LIBRE_CONVERTER_URL=http://127.0.0.1:5019
SLIDE_IMAGE_BASE=http://localhost:5100
```

**Frontend (.env.local)**
```bash
VITE_API_URL=http://localhost:5100
VITE_PYTHON_URL=http://localhost:8000
VITE_SUPABASE_URL=https://your-project.supabase.co
VITE_SUPABASE_ANON_KEY=eyJhbG...
```

---

## Local Development

```bash
# Terminal 1: LibreOffice converter
cd tools
docker-compose up lo-converter

# Terminal 2: .NET API
cd apps/api-dotnet
dotnet run

# Terminal 3: Python Flask
cd apps/python-semantic
source .venv/bin/activate
flask run --port 8000

# Terminal 4: Frontend
cd apps/web-frontend
npm run dev
```

Access: http://localhost:5173

---

## Key Design Decisions

### Element Keys
Format: `{slidePath}#{elementType}#{elementId}`  
Example: `ppt/slides/slide3.xml#textbox#5`  
Enables precise targeting of elements for redaction.

### Action Types
1. **Replace**: Find-and-replace (keyword or regex)
2. **Rewrite**: LLM-powered text transformation (TODO)

### Scope Definition
- **Range**: `{ "from": 1, "to": 5 }` - slides 1-5
- **List**: `{ "list": [1, 3, 7] }` - specific slides
- **All**: `{}` - all slides

### In-Memory Processing
PPTX redaction happens in-memory without temp files for security and performance.

### Warm LibreOffice Container
Keeps LibreOffice process alive for ~15x faster conversions (0.5s vs 8s).

### Vector Search Strategy
- Embeddings: text + image summaries + table summaries
- Index: ivfflat with 100 lists (balance speed/accuracy)
- Threshold: 0.2 minimum similarity

---

## Security Notes

**Current**: Prototype without authentication  
**Production TODO**:
- Implement Supabase Auth or OAuth2
- Row-level security policies
- Signed URLs for storage downloads
- Rate limiting on all endpoints
- Input validation and sanitization

---

## Performance

### Optimizations
- In-memory caching (5min TTL)
- Bulk database inserts
- Parallel embedding generation
- PDF preview caching
- ivfflat index for vector search

### Typical Latencies
- Upload (20 slides): ~10-15s
- Rule generation: ~2-3s
- Preview: ~1-2s
- Q&A: ~2-4s
- Vector search: <100ms

---

*Last updated: October 5, 2025*
