# Forge AI Metrics — POC (.NET 10 + EF Core)

POC implementation of the architecture in `2026-04-28-forge-ai-metrics-architecture.md`.

## Quick start

```bash
cp .env.example .env
docker compose up -d --build
open http://localhost:8000/swagger
```

Onboarding (developer machine):

```bash
curl -s "http://localhost:8000/setup/<TEAM_ID>/<API_KEY>" | bash
```

See `2026-04-28-forge-ai-metrics-architecture.md` for the full architecture.

<!-- pipeline test 2026-04-28T19:32:50Z -->
