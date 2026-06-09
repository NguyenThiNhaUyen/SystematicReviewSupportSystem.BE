# Safe Production Deploy

Production data is stored in the Docker volume `systematic-review-db-data`.
Do not run `docker compose down -v` on production.

Before deploying, create a database backup:

```bash
mkdir -p backups
docker compose exec -T db pg_dump -U postgres "SRSS.IAM" > backups/srss_$(date +%F_%H-%M).sql
```

Deploy from the VPS:

```bash
git pull origin dev
docker compose up -d --build
```

The API container must connect to PostgreSQL through the Docker network:

```text
Host=systematic-review-db;Port=5432;Username=postgres;Password=12345;Database=SRSS.IAM
```

Do not use `localhost` or host port `5433` from inside the API container.

Post-deploy checks:

```bash
curl http://localhost:8080/health
curl http://localhost:8080/health/db
curl -X POST http://localhost:8080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"keyLogin":"admin@srss.com","password":"123456"}'
```
