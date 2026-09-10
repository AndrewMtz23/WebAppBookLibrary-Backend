# Staff query filters

This delivery adds backend queries for staff lists. The administrative screens and dashboard aggregates remain pending.

| Endpoint | Additions |
|---|---|
| GET /api/books | Staff-only isActive, lowStock and missingResource filters. Readers remain scoped to active books. |
| GET /api/admin/users | Registration and last-login half-open UTC intervals; createdAt, lastLoginAt, username or role sorting; asc/desc direction. Admin only. |
| GET /api/loans | Search by title, username, display name or loan ID; dueFrom/dueTo; reservedAt/returnedAt/cancelledAt date selection; reservedAt/dueAt ordering; outstanding includes active and overdue. Admin/librarian only. |
| GET /api/loans/my | Same compatible query contract, always scoped to the authenticated reader. |

Staff loan rows optionally include bookTitle, username and displayName. No email, credential or resource URL is added to these responses. Effective overdue states and legacy date/media fallbacks are applied before sorting, pagination and counts, without rewriting stored loans. Queries are capped at 200 characters and page sizes at 100, with stable ID tie breakers and accurate totals for out-of-range pages.

Low stock means an active physical title with one available copy. Missing resource means an active digital title failing a server-side structural HTTPS check. The check supports international DNS hosts, userinfo, IPv6 without a zone, numeric ports through 65535 and escaped/unescaped paths. It does not test reachability or fully reproduce System.Uri normalization for unusual authorities, including scoped and IPv4-embedded IPv6. Writes retain the existing domain validator.

## Verification

From the backend repository:

```powershell
dotnet test WebAppBookLibrary.sln --configuration Release -p:UseAppHost=false --nologo
dotnet build WebAppBookLibrary.sln --configuration Release -p:UseAppHost=false --nologo
```

Persistence fixtures require an isolated MongoDB replica set listening only on localhost, port 27184, named booklibraryqa. Enable them explicitly:

```powershell
$env:BOOK_LIBRARY_TEST_MONGO_URI='mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa'
dotnet test WebAppBookLibrary.sln --configuration Release -p:UseAppHost=false --nologo
Remove-Item Env:BOOK_LIBRARY_TEST_MONGO_URI
```

Fixtures assert that exact local URI, create uniquely named temporary databases and drop only their own database in finally. They never use operational Atlas. Without the variable, five persistence tests are explicitly skipped.

Verified September 9, 2026: full local Mongo run 180 passed, zero failures/skips; Release build zero warnings/errors. HTTP fixtures use production authorization policies and cover denied roles, allowed staff, reader identity scoping and actual Mongo-backed catalog visibility. Regressions cover legacy/effective loan states, date ranges, malformed/accepted resource authorities and extreme pagination.

## Operational limits

The joined search converts foreign ObjectIds to strings and may not use their indexes directly. Review query plans before large-scale deployment. Overdue evaluation uses Mongo's request clock; a due time crossing during response construction can change the displayed state. No schema migration, data backfill, TTL, frontend change or dashboard aggregate is included.
