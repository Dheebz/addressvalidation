# Local API saturation rehearsal — 4 October 2026

The national local stack ran on one OrbStack host. The probe sent a repeating mix of G-NAF ID lookup, suggestions, match, positive validation, and negative validation through the public HAProxy endpoint. Each stage scheduled requests at a fixed rate with fresh HTTP connections. Raw request traces are compressed in [quality/results](quality/results) as `saturation_*.json.gz`; the generator is [quality/load_probe.py](quality/load_probe.py).

| Target requests/min | Duration scheduled | Completed | Non-200 | Achieved requests/s | p95 | Result |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 60,000 | 45 s | 45,000 | 0 | 998.9 | 17.1 ms | Clean sustained run |
| 72,000 | 45 s | 54,000 | 0 | 1,198.1 | 21.4 ms | Highest clean sustained run tested |
| 78,000 | 30 s | 39,000 | 3 HTTP 503 | 1,297.9 | 30.5 ms | First errors in sustained sweep |
| 96,000 | 20 s | 32,000 | 0 | 1,596.1 | 52.2 ms | Short run succeeded |
| 96,000 | 30 s | 48,000 | 10,527 HTTP 503 | 803.2 | 1,325 ms | Repeat collapsed; 59.8 s to drain |
| 102,000 | 20 s | 34,000 | 6,255 HTTP 503 | 937.2 | 1,207 ms | Overloaded; 36.3 s to drain |
| 108,000 | 20 s | 36,000 | 9,632 HTTP 503 | 880.0 | 1,102 ms | Overloaded; 40.9 s to drain |
| 132,000 | 20 s | 44,000 | 9,536 HTTP 503 | 854.3 | 1,613 ms | Overloaded; 51.5 s to drain |

All errors at the heavy stages were structured `DATASTORE_UNAVAILABLE` responses. PostgreSQL logged `sorry, too many clients already`. Its local `max_connections` is 100. After the runs, 93 idle connections were held by two client IPs, leaving little capacity for new sessions. This is a database connection limit, not an observed crash of the API or database. The gateway returned to HTTP 200 readiness after each run; a final recovery probe completed 1,000 requests at 100 requests/s with zero errors and p95 9.2 ms.

There was also a transient outlier: the first 200 requests/s run had 12 HTTP 503 responses and p95 318 ms; a 30-second repeat at the same rate had no errors and p95 11.8 ms. The 1,600 requests/s short run similarly does not establish sustainable capacity because its longer repeat failed. The result to use today is **72,000 requests/minute completed without errors for 45 seconds**. Failures appeared at **78,000 requests/minute** in a 30-second test and became severe in higher-rate repeats. These are observed local results, not a production service level or an exact cliff.

The probe and services shared one host, the runs were short, requests opened fresh HTTP connections, and client concurrency changed across stages. Achieved rate includes the time needed to drain queued requests, which is why overloaded stages show a much lower number than their target. A longer, external load test with realistic connection reuse and measured database session limits is needed before setting a production capacity target. The next engineering change to test is a connection budget across API replicas and the worker, with admission control or backpressure before PostgreSQL exhausts its slots.

## Connection budget follow-up

The local Compose configuration now caps pools at 32 per API replica, 8 each for the worker and admin, and 4 for the optional loader. A repeat at 1,200 requests/s sent 36,000 requests over 30 seconds with zero errors and p95 23.3 ms. A repeat at 1,600 requests/s sent 48,000 requests over 30 seconds: 37 client requests timed out, p95 reached 1,134.5 ms, and draining took 45.1 seconds. PostgreSQL had 74 total sessions after that run, with 32 idle sessions from each API and no new `too many clients already` log messages. The connection cap removed the database rejection mode; it did not make 1,600 requests/s sustainable. Its next bottleneck needs a separate client and server profiling pass. Full follow-up traces are `pool_budget_*.json.gz` in [quality/results](quality/results).

## Admission control follow-up — 5 October 2026

Address endpoints now have a zero-queue concurrency limit of **16 per API replica**. Excess requests receive HTTP 429 with `API_BUSY` and `Retry-After: 1`. A separate Python client container used reused HTTP connections through HAProxy. The request-level traces are `limited_1200_16.json.gz` and `limited_1600_16.json.gz`; the generator is [quality/keepalive_probe.py](quality/keepalive_probe.py).

| Target requests/s | Scheduled | HTTP 200 | HTTP 429 | Transport errors | Achieved requests/s | p95 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,200 for 20 s | 24,000 | 23,496 | 504 | 0 | 1,198.8 | 50.2 ms |
| 1,600 for 20 s | 32,000 | 29,347 | 2,653 | 0 | 1,597.9 | 63.1 ms |

All 429 responses carried `API_BUSY` and the expected retry header. A preliminary 32-slot run at 1,200 requests/s still had 11 client timeouts, which prompted the tighter limit. The new result demonstrates predictable rejection at these two local rates; it does not show that every request succeeds at 1,600 requests/s, establish a minute-long zero-error ceiling, or predict performance on separate hosts or Azure. Clients must back off and retry eligible operations when they receive 429.
