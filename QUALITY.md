# Local address quality baseline

The [August 2026 golden corpus](quality/golden_2026_08.json) contains real addresses from the locally installed full national G-NAF serving table. It covers all eight states and territories, units, apartment wording, levels, a suite, a shop with a ranged street number, a named building, an official street alias, and eleven deliberately incorrect inputs. Expected G-NAF IDs were checked against unique formatted addresses in the serving table.

Run it against the local stack:

```sh
python3 quality/run_golden.py
```

The runner first checks that `/v1/metadata` reports release `2026_08`. For each positive case it exercises ID lookup, suggest, match, validate, and format. It checks the expected G-NAF ID, canonical line, match threshold, postal-delivery disclaimer, and selected structured components. Negative cases must not validate or produce a matched format result. Nonzero exit status means a case failed. The printed request timings are diagnostic samples, not a throughput or service-level benchmark.

To compare synchronous and bulk results, run `python3 quality/run_bulk_parity.py`. It submits five small local jobs covering a unit, a level, a ranged shop address, scored suggestions with source references, nonmatches, an unknown G-NAF ID, and an invalid input row. It compares bulk components, coordinates, IDs, scores, status, and release context with the corresponding synchronous responses. This command writes small input and output blobs to local Azurite, so run it when verifying a worker change rather than on every request.

These cases are a regression baseline derived from G-NAF, not independent proof that the source addresses are physically deliverable or that a 0.8 confidence threshold is calibrated. The ambiguous `BELCONNEN` locality alias maps to many ACT localities, so the matcher only expands aliases with a unique canonical target in the relevant state and locality. Before production, grow the corpus with human-reviewed ambiguous and near-miss examples from real user inputs and assess false positives and false negatives. G-NAF matching never verifies postal delivery.

The [sustained local traffic and failover rehearsal](LOAD-REHEARSAL.md) records the 20-request-per-second baseline, API and database failovers, and a live release switch. The [local saturation rehearsal](SATURATION-REHEARSAL.md) adds stepped throughput measurements and identifies PostgreSQL connection exhaustion under heavy traffic. Longer external capacity testing remains.

The [test and coverage guide](TESTING.md) describes the disposable database suite, coverage floors, and seeded API contract check. Its one-command local gate runs without touching the installed national release.
