# BookLibrary API

REST API for a production-minded hybrid library. It handles physical and digital books, JWT authentication, role-based administration, atomic reservations, favorites, operational dashboards, schema migration, and restricted audit logs.

## Highlights

- ASP.NET Core 8, MongoDB, JWT bearer authentication, and Swagger
- Public registration always creates a `user`; privileged roles are assigned directly in the database
- Explicit authorization policies for users, librarians, and administrators
- Atomic physical inventory and concurrent digital reservations with explicit loan states
- Server-side catalog pagination, filters, stable sorting, favorites, user administration, and role dashboards
- Idempotent book-schema migration CLI with dry-run as the default
- RFC 7807-style error responses, input validation, authentication rate limiting, CORS, and a public health endpoint
- Automated test suite covering authentication, authorization, validation, audit sanitization, duplicate registration races, and loan concurrency

## Role matrix

| Capability | user | librarian | admin |
| --- | :---: | :---: | :---: |
| Browse books | yes | yes | yes |
| Borrow and return own books | yes | no | no |
| Create and edit books | no | yes | yes |
| View all loans | no | yes | yes |
| Delete books or loans | no | no | yes |
| View audit logs | no | no | yes |
| Manage users | no | no | yes |
| View role dashboard | reader | operations | administration |

## Run locally

Requirements: .NET 8 SDK and a MongoDB Atlas database.

1. Create a local `.env` beside `WebAppBookLibrary.sln`.
2. Set `MONGO_USER`, `MONGO_PASSWORD`, `MONGO_CLUSTER`, `MONGO_DATABASE`, `JWT_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`, and `CORS_ORIGIN`.
3. Start the API:

```bash
dotnet restore
dotnet run --project WebAppBookLibrary/WebAppBookLibrary.csproj
```

The default development URL is `https://localhost:7086`; Swagger is available at `/swagger`. The frontend proxy targets this URL.

Never commit `.env`. For production, inject secrets through the hosting platform and restrict `CORS_ORIGIN` to the deployed frontend.

## API surface

| Method | Endpoint | Access |
| --- | --- | --- |
| `GET` | `/api/health` | Public |
| `POST` | `/api/auth/register` | Public, rate limited |
| `POST` | `/api/auth/login` | Public, rate limited |
| `GET` | `/api/books?query=&genre=&mediaType=&language=&available=&page=&pageSize=&sort=&direction=` | Authenticated |
| `GET` | `/api/books/{id}` | Authenticated |
| `POST` | `/api/books` | Librarian or admin |
| `PUT` | `/api/books/{id}` | Librarian or admin |
| `PATCH` | `/api/books/{id}/status` | Librarian or admin |
| `DELETE` | `/api/books/{id}` | Admin; compatibility alias for logical deactivation |
| `POST` | `/api/loans` | User |
| `GET` | `/api/loans/my?page=&pageSize=&status=&mediaType=` | User |
| `GET` | `/api/loans?page=&pageSize=&status=&mediaType=&userId=&bookId=` | Librarian or admin |
| `GET` | `/api/books/{id}/digital-access` | User with an active digital reservation |
| `PUT` | `/api/loans/{id}/return` | Owner, librarian, or admin |
| `PUT` | `/api/loans/{id}/cancel` | Owner, librarian, or admin |
| `DELETE` | `/api/loans/{id}` | Admin |
| `GET` | `/api/favorites?page=&pageSize=` | User |
| `POST`, `DELETE` | `/api/favorites/{bookId}` | User |
| `GET` | `/api/admin/users`, `/api/admin/users/{id}` | Admin |
| `PUT` | `/api/admin/users/{id}/role`, `/api/admin/users/{id}/status` | Admin |
| `GET` | `/api/dashboard/reader` | User |
| `GET` | `/api/dashboard/librarian` | Librarian or admin |
| `GET` | `/api/dashboard/admin` | Admin |
| `GET` | `/api/log/recent`, `/api/log/count/{level}` | Admin |

New phase-2 endpoints return explicit response DTOs in camelCase. Errors use Problem Details and never expose internal exception messages, credential hashes, schema fields, or loan correlation fields.

## Book schema migration

Back up or snapshot Atlas before applying a migration. Analysis is read-only by default:

```bash
dotnet run --project WebAppBookLibrary.Migration -- --dry-run
```

An actual update requires both switches and must only run after the dry-run report has been reviewed:

```bash
dotnet run --project WebAppBookLibrary.Migration -- --apply --snapshot-confirmed
```

Rollback consists of restoring the confirmed Atlas snapshot and recreating the documented indexes. The CLI reports only counts, identifiers, and anomaly codes; it does not print descriptions, emails, credentials, or private URLs.

## Verification

```bash
dotnet build WebAppBookLibrary.sln -c Release
dotnet test WebAppBookLibrary.sln -c Release --no-build
```

GitHub Actions runs the same restore, build, and test checks on pushes and pull requests.

## Architecture

Controllers own HTTP contracts and authorization; services own business rules; store abstractions isolate MongoDB operations. Audit entries intentionally exclude credentials, tokens, and exception internals. The in-memory EF Core context is used only by the audit service infrastructure; library data lives in MongoDB.

See [`docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md`](docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md) for the hardening decisions and threat model.

## Current boundaries

This iteration intentionally does not include refresh tokens, password recovery, email verification, MFA, DRM, Docker, or cloud deployment. Dashboards expose real MongoDB aggregates and are not a penetration test or security certification.
