# Staff circulation

The staff routes `/librarian/loans` and `/admin/loans` use server filtering and pagination. Each role owns its page composition and shares typed circulation components and transport. The reader routes and mutation contracts remain compatible.

## Detail contract

`GET /api/loans/{id}` requires `ViewAllLoans`. Invalid identifiers return 400, missing loans 404 and readers 403. The response contains `loan`, `history`, `historyTruncated`. Safe loan names are projected on the server; notes, email, credentials and digital resources are not exposed in the detail.

History contains at most 50 recent matching audit records plus up to three missing recorded transition dates, sorted chronologically. Events have `eventType` (created/returned/cancelled), `timestamp`, optional `actorUsername`, and `source` (audit/recorded-date). The event/target whitelist excludes unrelated logs. Timestamp and actor are not fabricated for missing audit records; persisted dates are explicitly marked and lack an actor. `historyTruncated` signals omitted audit records. ObjectId matching tolerates request and historic stored target casing.

## Transitions

Existing staff permissions allow return and cancellation; the UI offers return for physical items and cancellation for active/overdue reservations. The same completed transition is idempotent, and the opposite terminal transition returns 409. Effective legacy status is used consistently with list/detail. Physical completion and inventory release remain transactional. A network timeout must be reconciled with a GET before attempting another mutation.

## Verification and rollback

Relevant backend tests: StaffLoanDetailTests, StaffCirculationTransitionTests, existing HybridLoanServiceTests and Mongo persistence suites. Opt-in persistence tests accept only `mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa` and use isolated fixture databases. No Atlas migration is required.

Rollback is additive: revert the circulation route/components and detail extension together, retaining loan and audit records. Do not restore inventory manually from UI counts. Broader dashboards, user administration, logs/security and integrated phase acceptance are separate deliveries.

## Verificación de entrega (2026-09-11)
Frontend: 135 pruebas y build de producción correctos. Backend: 195 pruebas con MongoDB local, ninguna omitida; Release sin advertencias ni errores. Regresión HTTP: 13 comprobaciones correctas. Revisiones independientes cerradas. Pruebas manuales parciales de devolución librarian y cancelación admin con auditoría real; aceptación integral de fase 4 pendiente.
