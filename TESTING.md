# Test and CI guide

Run these checks before changing CI/CD. They cover the address rules, release lifecycle, official-source selection, the seeded API contract, and the installed national dataset.

## GitHub CI

The [CI workflow](.github/workflows/ci.yml) runs on pushes, pull requests, and manual dispatch. It has three jobs:

1. **Format and lint:** `dotnet format`, Ruff lint/format for Python, and ShellCheck for shell scripts.
2. **Build, tests, and coverage:** Release build, unit and PostgreSQL integration tests, and the business-code coverage floors below. Cobertura reports are retained as a 14-day workflow artifact.
3. **Containers and service smoke:** Validate both Compose files, build every service image, start the disposable database/API/admin stack, seed and promote synthetic data, and check the API and admin contracts. The workflow always removes its disposable stack.

The container check runs after the style and test checks pass. Actions are pinned to full commit hashes, the token is read-only, and concurrent runs on the same branch cancel older runs. Configure branch protection to require all three checks once the repository is on GitHub. This workflow does not deploy to Azure; production deployment remains deferred until the local product and Azure design are ready. A Git repository and GitHub remote are still needed before hosted CI can execute.

To reproduce the lightweight style checks locally, install Ruff 0.16.10 and ShellCheck, then run:

```sh
ruff check --select E4,E7,E9,F,I quality scripts
ruff format --check quality scripts
shellcheck scripts/*.sh infra/postgres/*.sh
```

The full disposable .NET and container gate is below. For exact CI build commands, see the workflow.

From the project root, `sh scripts/run-local-gate.sh` runs the disposable unit/integration tests, coverage floors, synthetic seed guard, seeded API and admin smoke checks, and formatting verification, then removes its disposable containers and volumes. It refuses to run when a disposable test stack already exists, so it cannot remove one you are inspecting.

| Check | Data used | Main risks covered |
| --- | --- | --- |
| `Address.Core.Tests` | In-memory records | Normalisation, units and levels, confidence guards, structured components, formatting |
| `Address.Integration.Tests` | Disposable PostgreSQL database, generated PSV ZIP, and in-memory admin data | Migrations, release promotion/rollback, failed imports, raw cleanup, bulk job states, catalogue selection and download resume, admin release controls, token fields, and HTML escaping |
| `run_seed_smoke.py` | Isolated synthetic stack | API response contract, readiness rule, ID, suggest, match, validate, 404 |
| `run_admin_smoke.py` | Isolated synthetic stack | Admin readiness, dashboard, two-node cluster status, antiforgery rejection |
| `run_golden.py` | Installed August 2026 national release | All states and territories, real IDs, positive and negative matches, components |
| `run_bulk_parity.py` | Installed national release and local Azurite | Bulk/synchronous agreement for ID, suggest, match, validate, format |
| Load and failover rehearsals | Installed national release | Throughput degradation, API and database takeover, release switch under traffic |
| `rehearse-backup-restore.sh` | Isolated synthetic HA stack | Compressed logical backup, restore checks, primary takeover, former-primary rejoin, continuous API recovery |
| `rehearse-national-restore.sh` | Installed national source; disposable PostgreSQL target | Full logical dump/restore, active and previous releases, address/alias counts, sample G-NAF ID |

## Unit, integration, and coverage

Start only the disposable database cluster. The integration suite creates and drops a unique database inside it; it must never point at the national database.

```sh
docker compose -f compose.auto-ha-test.yaml up -d pg-a pg-b
docker run --rm --network addressvalidation-auto-ha-test_default \
  -v "$PWD:/src" -w /src \
  -e ADDRESS_TEST_CONNECTION='Host=pg-a;Database=postgres;Username=postgres;Password=address' \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test AddressValidation.slnx -c Release \
  --collect:'XPlat Code Coverage' --settings quality/coverage.runsettings \
  --results-directory /src/quality/coverage/run --nologo
python3 quality/check_coverage.py quality/coverage/run
docker compose -f compose.auto-ha-test.yaml down -v
```

Use a new results directory, or remove an old `quality/coverage/run` directory before collecting again. The coverage checker reads all Cobertura reports in that directory. The current floors are **90% lines and 60% branches for `Address.Core`**, and **80% lines and 60% branches for `Address.Data`**. Generated regex and compiler code is excluded. The reports are separate per test project; the checker uses the strongest measured floor for each assembly, which cannot exceed the union of all tests. The API and loader are exercised by smoke checks, and the admin page has direct rendering tests. The automated admin HTTP smoke check verifies `/health/ready`, dashboard and cluster responses, and rejection of an action without a form token. An earlier manual check also confirmed that a token-bearing request for a nonexistent release is rejected without changing data. The API, worker, admin, and loader host projects do not yet have an instrumented line-coverage target. Do not present the business-code percentages as whole-product coverage.

## Seeded API contract

Follow [DATABASE-SEEDING.md](DATABASE-SEEDING.md) to build images, start the disposable API, seed and promote `demo_1`, and run `python3 quality/run_seed_smoke.py --version demo_1`. The smoke check expects `/health/ready` to remain 503 for synthetic data while address endpoints work. Remove the disposable volumes after use.

## Installed national checks

With the full August 2026 release active on the main local stack:

```sh
python3 quality/run_golden.py
python3 quality/run_bulk_parity.py
```

These checks require the national data and local Azurite, so they are separate from the disposable tests. A passing score test shows agreement with the current dataset and expected IDs; it does not establish postal deliverability or a calibrated false-positive rate. Add independently reviewed ambiguous inputs before production. The [load rehearsal](LOAD-REHEARSAL.md) and [saturation rehearsal](SATURATION-REHEARSAL.md) are repeatable operational measurements, not per-change unit tests.
