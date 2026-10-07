.PHONY: up down test logs
up:
	docker compose up --build
down:
	docker compose down
test:
	dotnet test
logs:
	docker compose logs -f
