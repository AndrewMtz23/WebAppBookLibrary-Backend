# BookLibrary API

REST API for a portfolio-grade digital library. It handles user registration and JWT login, role-based book administration, concurrency-safe loans, and restricted audit logs.

## Highlights

- ASP.NET Core 8, MongoDB, JWT bearer authentication, and Swagger
- Public registration always creates a `user`; privileged roles are assigned directly in the database
- Explicit authorization policies for users, librarians, and administrators
- Atomic book reservation linked to the active loan, with rollback on partial failures
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

## Run locally

Requirements: .NET 8 SDK and a MongoDB Atlas database.

1. Copy `.env.example` to `.env`.
2. Set `MONGO_USER`, `MONGO_PASSWORD`, `MONGO_CLUSTER`, and a strong `JWT_KEY` (at least 32 characters).
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
| `GET` | `/api/books` and `/api/books/{id}` | Authenticated |
| `POST`, `PUT` | `/api/books` | Librarian or admin |
| `DELETE` | `/api/books/{id}` | Admin |
| `POST` | `/api/loans` | User |
| `GET` | `/api/loans/my` | User |
| `GET` | `/api/loans` | Librarian or admin |
| `PUT` | `/api/loans/{id}/return` | Owner, librarian, or admin |
| `DELETE` | `/api/loans/{id}` | Admin |
| `GET` | `/api/log/recent`, `/api/log/count/{level}` | Admin |

Responses that return resources use `{ "message": "...", "data": ... }`. Errors use Problem Details and never expose internal exception messages.

## Verification

```bash
dotnet build WebAppBookLibrary.sln -c Release
dotnet test WebAppBookLibrary.sln -c Release --no-build
```

GitHub Actions runs the same restore, build, and test checks on pushes and pull requests.

## Architecture

Controllers own HTTP contracts and authorization; services own business rules; store abstractions isolate MongoDB operations. Audit entries intentionally exclude credentials, tokens, and exception internals. The in-memory EF Core context is used only by the audit service infrastructure; library data lives in MongoDB.

See [`docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md`](docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md) for the hardening decisions and threat model.
