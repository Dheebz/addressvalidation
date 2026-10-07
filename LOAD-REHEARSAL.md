# Local traffic and failover rehearsal — 4 October 2026

This rehearsal used the full national G-NAF stack on one OrbStack host. The load probe sent 20 requests per second with eight concurrent clients. It cycled through exact G-NAF ID lookup, suggestions, match, positive validation, and negative validation. Each scenario used fresh HTTP requests through the public HAProxy endpoint at port 8080. Request-level evidence is in [quality/results](quality/results); the repeatable traffic generator is [quality/load_probe.py](quality/load_probe.py).

| Scenario | Requests | Non-200 | p50 | p95 | p99 | Longest gap between successful responses |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Stable baseline, 45 s | 900 | 0 | 6.2 ms | 31.3 ms | 57.8 ms | 0.18 s |
| API A stopped and restored, initial gateway | 1,300 | 0 | 6.9 ms | 44.0 ms | 3,025.2 ms | 2.36 s |
| Database A primary stopped; B promoted | 2,000 | 44 HTTP 500 | 6.5 ms | 35.1 ms | 244.1 ms | 2.70 s |
| May release activated by rollback, then August restored | 1,300 | 0 | 7.7 ms | 43.0 ms | 184.8 ms | 0.70 s |
| Database B primary stopped; A promoted, after error mapping | 1,200 | 44 HTTP 503 | 9.7 ms | 68.3 ms | 261.3 ms | 2.59 s |
| API A stopped and restored, 1 s gateway connect timeout | 1,200 | 0 | 9.0 ms | 131.2 ms | 1,065.9 ms | 0.48 s |

The database error window in the first failover ran from 18.205 to 20.223 seconds after the probe began. In the second it ran from 16.931 to 18.856 seconds. Both nodes rejoined healthy with zero reported replay lag. All 44 non-200 responses in the second run were structured `DATASTORE_UNAVAILABLE` errors with HTTP 503 and `Retry-After: 2`. These responses are explicit temporary failures; automatic database takeover does not guarantee every in-flight request succeeds.

During both API replica tests, the other replica served all requests. The local gateway's backend connection timeout was reduced from three seconds to one second after the first run. The second run's p99 was about one second rather than three, though the p95 was higher; these are separate local runs and should not be treated as a controlled before/after benchmark.

The release test switched the database serving view from August 2026 to the retained May 2026 release and back under load. All requests succeeded. August is active again, May remains retained, both API replicas are ready, and the database cluster is healthy. The [national quality corpus](QUALITY.md) passed after the rehearsal.

These numbers describe this single computer at 20 requests per second. They are a baseline, not a capacity limit, cloud availability claim, or Azure cost estimate. The later [local saturation rehearsal](SATURATION-REHEARSAL.md) tested higher rates. Before production, test longer runs, realistic client connection reuse, and retry behaviour with real callers. A separate human-reviewed set of ambiguous and near-miss inputs is still needed to calibrate the 0.8 match threshold.
