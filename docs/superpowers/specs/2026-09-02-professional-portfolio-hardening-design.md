# Professional Portfolio Hardening Design

**Date:** 2026-09-02  
**Status:** Approved in conversation  
**Applies to:** `WebAppBookLibrary` backend and `Book-Library-Client` frontend

## Objective

Turn the existing Book Library application into a credible, demonstrable portfolio project. The result must compile reliably, enforce its advertised authorization rules, preserve loan consistency, report security checks honestly, contain meaningful automated tests, and document only behavior that actually exists.

## Scope

This iteration will:

- retain Angular 17, ASP.NET Core 8, and MongoDB;
- make public registration create only `user` accounts;
- normalize roles and enforce permissions in the API;
- protect the catalog consistently in both frontend and backend;
- make JWT configuration and expired-session handling consistent;
- prevent duplicate concurrent loans and recover safely from partial failures;
- replace the fabricated secure/insecure comparison with observable checks against the real API;
- standardize API routes and response contracts;
- fix clean-install builds;
- add focused backend and frontend tests;
- add continuous-integration workflows for both repositories; and
- update Swagger and repository documentation to match the implementation.

## Non-goals

This iteration will not add refresh tokens, email delivery, email verification, password reset, MFA, a user-administration UI, cloud deployment, Docker, or a second deliberately vulnerable API. Privileged roles will be assigned directly in MongoDB for now.

## Architecture

The two existing repositories remain independent. The Angular SPA communicates with the ASP.NET Core REST API, and the API owns all authorization and business rules. MongoDB remains the persistent store for users, books, loans, and audit logs.

Controllers will accept request DTOs and return explicit response DTOs rather than accepting MongoDB entities as public input. Services will own business rules. MongoDB-specific persistence will remain behind the service boundary so core decisions can be tested without live Atlas credentials.

All application endpoints will use the `/api` prefix. The intended public surface is:

- `POST /api/auth/register`
- `POST /api/auth/login`
- `GET /api/health`
- authenticated book, loan, log, and security-report endpoints

The frontend development proxy and environment configuration will use these routes consistently.

## Authentication and authorization

Public registration will no longer accept a role. The backend will set the normalized role `user` unconditionally. The Angular registration form will not display a role selector.

The canonical stored and emitted role values are lowercase:

- `user`
- `librarian`
- `admin`

Authorization policies will express permissions without duplicating upper- and lowercase role names throughout controllers.

| Capability | user | librarian | admin |
| --- | --- | --- | --- |
| View catalog | yes | yes | yes |
| Borrow available book | yes | no | no |
| View own loans | yes | no | no |
| View all loans | no | yes | yes |
| Return own loan | yes | no | yes |
| Return any loan | no | yes | yes |
| Create or edit books | no | yes | yes |
| Delete books or loans | no | no | yes |
| View logs and security report | no | no | yes |

JWT validation and token generation will consume the same validated settings. Secrets may come from environment variables or ignored local settings, but never from committed production credentials. The frontend will detect token expiry, clear stale authentication state, redirect to login on `401`, and avoid treating the mere presence of a token as a valid session.

Public authentication endpoints will receive basic rate limiting. API error responses will use a consistent problem-details shape and will not expose exception messages or stack traces.

## Loan consistency

A loan may be created only when an authenticated active `user` requests an existing book whose `isAvailable` value is currently `true`.

The availability transition will be conditional and atomic: the update succeeds only for a still-available book. Only the caller that wins that transition may create the loan. If loan persistence fails after the reservation, the service will attempt to restore availability and log the failure. This prevents the current double-loan race without requiring a new infrastructure dependency.

Return operations will:

1. validate the loan identifier;
2. verify that the loan exists and is active;
3. verify ownership or a privileged role;
4. mark the loan returned once; and
5. restore book availability.

Repeated returns must be idempotent from the user's perspective and must not corrupt book availability. Dates remain UTC, and the due date remains 14 days after the loan date.

## Honest security and health dashboard

The dashboard will analyze only the real API. It will not refer to a missing insecure service and will contain no random values, forced scores, altered timings, or unsupported claims.

An admin-triggered run will execute a fixed set of observable probes without attaching the admin JWT where anonymous behavior is being checked:

- the health endpoint returns a successful response;
- anonymous catalog access is rejected;
- anonymous loan access is rejected;
- anonymous log access is rejected; and
- a users-list endpoint is not publicly exposed.

Each probe records its actual HTTP status, elapsed time, timestamp, and an explanation. Its result is one of:

- `passed`: observed behavior matches the declared expectation;
- `failed`: observed behavior contradicts the expectation;
- `inconclusive`: the response cannot establish the claimed control; or
- `unavailable`: the API could not be reached.

The score is `passed / (passed + failed) * 100`. Inconclusive and unavailable probes are displayed but excluded from the denominator. If there are no conclusive probes, no numeric score is shown. The dashboard must describe itself as a lightweight demonstrator, not a penetration test or security certification.

The duplicate dashboard implementation will be removed. The retained standalone component will use external template and style files to keep responsibilities understandable.

## Frontend contracts and errors

Services will type the backend envelope `{ message, data }` instead of declaring raw arrays while components compensate dynamically. Authentication errors, expired sessions, and general API failures will have a single handling path. Temporary console debugging will be removed.

The unused or incomplete runtime-configuration path will either be wired consistently or removed; there will be one authoritative API URL mechanism. The invalid TypeScript compiler setting and incorrect environment import will be corrected. A clean dependency installation followed by a production build must succeed without application errors.

## Backend validation and observability

Request DTOs will validate required values and sensible lengths. Invalid MongoDB identifiers will produce client errors instead of internal-server errors. Duplicate usernames and emails will produce deterministic conflict responses.

Audit entries will capture the authenticated username, controller/action, HTTP method, and remote IP when available. Sensitive credentials and raw JWTs must never be logged.

Swagger/OpenAPI will expose JWT authentication and document the actual `/api` routes. The health endpoint will reveal availability only and will not return secrets, connection strings, or exception details.

## Automated tests

Backend tests will run without real MongoDB credentials and cover at minimum:

- public registration always creates a `user`;
- privileged role input cannot be used to escalate privileges;
- password and email validation;
- authorization policy expectations;
- one concurrent reservation wins for an available book;
- persistence failure restores availability;
- users cannot return another user's loan;
- privileged staff can return loans allowed by the role matrix; and
- repeated return behavior is safe.

Frontend tests will cover at minimum:

- expired tokens are rejected and cleared;
- `401` responses end the local session;
- registration does not send a role;
- security probe statuses map to the correct result;
- the score uses only conclusive probes; and
- an unreachable API produces `unavailable`, not a vulnerability claim.

Tests must be deterministic and must not call production services.

## Continuous integration

Each repository will contain a GitHub Actions workflow.

The backend workflow will restore, build with warnings treated visibly, and run tests. The frontend workflow will use the lockfile for a clean install, run the noninteractive test suite, and create a production build. Workflows will not require MongoDB Atlas credentials.

## Documentation

Both README files will describe the real architecture, prerequisites, routes, role matrix, local setup, test commands, and security-dashboard limitations. Claims about refresh tokens, profiles, real-time updates, server-side search, migrations, or other absent features will be removed.

The portfolio presentation will emphasize verifiable qualities: authorization, password hashing, concurrency-safe lending, audit logging, typed frontend contracts, automated tests, CI, and honest operational checks.

## Acceptance criteria

The work is complete when:

1. public registration cannot create a privileged account;
2. direct anonymous requests to protected resources receive `401` or `403` as appropriate;
3. simultaneous attempts cannot create two active loans for one book;
4. the dashboard reports only observed results and uses the defined scoring formula;
5. backend tests pass without external credentials;
6. frontend tests pass noninteractively;
7. backend and frontend production builds succeed from restored dependencies;
8. CI workflows encode those same checks;
9. documentation matches the implemented routes and features; and
10. neither repository contains uncommitted generated artifacts or secrets.
