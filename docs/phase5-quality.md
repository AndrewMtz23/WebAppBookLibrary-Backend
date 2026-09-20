# Phase 5: verification and release operations

Updated 2026-09-19: **251 backend tests passed, zero skipped**, including real Mongo readiness, unavailable dependency, production auth throttling, route-safe metrics/correlation, and BSON migration/recovery rehearsal. Production NuGet dependencies report no known vulnerabilities. See [current operations and remaining external gates](release-operations.md). The sections below preserve the first-delivery commands and historical evidence; their older counts are not the latest result.

## Reproducible backend checks

Use .NET 8 and Docker. From this repository, create a dedicated loopback replica set (required by transactional integration tests):

```powershell
docker run -d --name booklibrary-mongo-qa -p 127.0.0.1:27184:27184 mongo:8.0.28 --replSet booklibraryqa --bind_ip_all --port 27184
docker exec booklibrary-mongo-qa mongosh --port 27184 --quiet --eval 'rs.initiate({_id:"booklibraryqa",members:[{_id:0,host:"127.0.0.1:27184"}]})'
docker exec booklibrary-mongo-qa mongosh --port 27184 --quiet --eval 'db.hello().isWritablePrimary'
```

Wait until Mongo responds before initialization and until `isWritablePrimary` is true before testing. If port 27184 is occupied, investigate; do not reuse an unknown database server. No production URI is accepted by the persistence test fixtures.

```powershell
dotnet restore WebAppBookLibrary.sln --locked-mode
dotnet build WebAppBookLibrary.sln -c Release --no-restore
$env:BOOK_LIBRARY_TEST_MONGO_URI = 'mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa'
dotnet test WebAppBookLibrary.sln -c Release --no-build --logger trx --results-directory TestResults --collect:'XPlat Code Coverage'
dotnet list WebAppBookLibrary.sln package --vulnerable --include-transitive
python scripts/check-sensitive-files.py
```

Local result: **240 passed, zero skipped**, Release build with zero warnings/errors, no reported vulnerable direct/transitive NuGet dependencies. The CI now initializes this replica set, enforces locked restore and NuGet audit warnings, rejects skipped tests, and uploads TRX/coverage. GitHub execution remains to be verified after publication.

The sensitive-file check blocks tracked `.env` variants (except examples/templates) and recognizable private keys, GitHub/AWS credentials and literal credential-bearing MongoDB URIs. It prints paths/types only. It is not an entropy scanner or a scan of Git history.

## Isolated browser fixture

```powershell
dotnet restore tools/BookLibrary.QaHost/BookLibrary.QaHost.csproj --locked-mode
dotnet run --project tools/BookLibrary.QaHost/BookLibrary.QaHost.csproj -c Release --no-launch-profile
```

This separate executable binds only `127.0.0.1:7184`, always uses the replica set above and a fresh `booklibrary_ui_test_<guid>` database. It does not load `.env`. It uses random signing material and fictitious identities/books, creates application indexes, and drops only its own database in `finally` when it shuts down normally. Abrupt process termination can leave that isolated database behind. Stop the host with Ctrl+C, then remove the dedicated Docker container when finished:

```powershell
docker rm -f booklibrary-mongo-qa
```

The host exposes `/__qa` only to identify the fixture to the browser suite; the production application does not have this endpoint. The browser suite deliberately disables authentication throttling in this host because every test shares loopback. It therefore does **not** validate the production five-attempt-per-minute auth limit. Do not deploy this host.

Start the frontend with its `proxy.e2e.json` and follow its `docs/phase5-quality.md`. The frontend workflow `Integrated browser QA` accepts the exact compatible backend ref, so browser evidence can identify both source versions.

## Rollout / rollback checklist

1. Record frontend/backend commit IDs and retain the previous deployable artifacts. Run all CI gates and the integrated browser workflow against those refs.
2. Obtain an Atlas snapshot and verify how it will be restored. First run migration without `--apply` against an authorized staging copy; retain counts. The tool refuses `--apply` without `--snapshot-confirmed`; that flag alone is not proof of a backup.
3. Reconcile counts and inventory, validate idempotence, then approve the database change for the target environment. Do not run this on production just to complete a checklist.
4. Deploy the compatible API before the client. Smoke-test each role, the unauthorized paths and a reserve/return round trip using dedicated test data. Observe error rates and latency.
5. For a client regression, restore the previous frontend artifact; for an API regression, restore its compatible artifact. Do not edit counters by hand. Database recovery uses the verified snapshot and affected-document record, with an explicit decision about writes made after the snapshot. Remove indexes individually only when justified by compatibility.

## Current release gates (2026-09-19)

- Backend CI passed on GitHub for `bd2cb8983fd30bffe7b69ac00f35dfd2bdd4dc6f`, run `35462182589`. Branch protection remains unconfigured.
- The client now contains all six required end-to-end journeys. Its current browser results and integrated CI evidence are recorded in the frontend quality document.
- Human assistive-technology and actual browser 200% zoom review remain open; automated keyboard, CSS magnification and axe checks do not certify WCAG conformance.
- Local latency baseline, proposed alert thresholds, liveness/readiness, production auth-throttling verification and redacted full-history secret scanning are implemented. See `release-operations.md`; telemetry export and operational alerts are not deployed.
- The BSON backup/migration/restore rehearsal passed only against disposable local MongoDB. Authorized staging snapshot, migration reconciliation, production rollout and recovery evidence remain external gates.
