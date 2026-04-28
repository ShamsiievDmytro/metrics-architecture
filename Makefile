.PHONY: up down logs restart api-shell db-shell test reseed

up:
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f api

restart:
	docker compose restart api

api-shell:
	docker compose exec api bash

db-shell:
	docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$$MSSQL_SA_PASSWORD" -d $(DB_NAME)

test:
	docker compose exec api dotnet test /src/Forge.Metrics.sln
