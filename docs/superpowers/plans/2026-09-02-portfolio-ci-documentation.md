# Portfolio CI and Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Encode the verified backend/frontend checks in CI and make both repositories accurately present the project to recruiters and contributors.

**Architecture:** Each independent repository owns a GitHub Actions workflow that mirrors its local clean-build commands. Each README documents only implemented behavior and links technical claims to reproducible commands and API contracts.

**Tech Stack:** GitHub Actions, .NET 8 CLI, Node.js 20, npm, Angular CLI, Markdown

**Spec:** `docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md`

## Global Constraints

- Workflows must run without MongoDB Atlas credentials.
- Workflows must not print or require secrets.
- Documentation must use canonical lowercase roles and `/api` routes.
- Do not claim refresh tokens, MFA, profiles, real-time updates, cloud deployment, Docker, server-side search, or penetration-test certification.
- CI commands must be the same commands proven locally in the backend and frontend plans.

---

### Task 1: Add backend continuous integration

**Files:**
- Create: `BackEnd/WebAppBookLibrary/.github/workflows/ci.yml`

**Interfaces:**
- Consumes: credential-free test seam and Release baseline from the backend plan.
- Produces: CI named `Backend CI` on pushes and pull requests.

- [ ] **Step 1: Create the backend workflow**

```yaml
name: Backend CI

on:
  push:
  pull_request:

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet restore WebAppBookLibrary.sln
      - run: dotnet build WebAppBookLibrary.sln --configuration Release --no-restore
      - run: dotnet test WebAppBookLibrary.sln --configuration Release --no-build
```

- [ ] **Step 2: Validate structure locally**

Run: `Get-Content -Raw .github/workflows/ci.yml`

Run: `dotnet test WebAppBookLibrary.sln --configuration Release`

Expected: YAML contains checkout, .NET 8 setup, restore, build, and test; local tests pass without MongoDB variables.

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/ci.yml
git commit -m "ci: verify backend build and tests"
```

### Task 2: Add frontend continuous integration

**Files:**
- Create: `FrontEnd/Book-Library-Client/.github/workflows/ci.yml`

**Interfaces:**
- Consumes: clean-install tests and production-build baseline from the frontend plan.
- Produces: CI named `Frontend CI` on pushes and pull requests.

- [ ] **Step 1: Create the frontend workflow**

```yaml
name: Frontend CI

on:
  push:
  pull_request:

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 20
          cache: npm
      - run: npm ci
      - run: npm test -- --watch=false --browsers=ChromeHeadless
      - run: npm run build -- --configuration production
```

- [ ] **Step 2: Validate the same commands locally**

Run from `FrontEnd/Book-Library-Client`:

```powershell
npm ci
npm test -- --watch=false --browsers=ChromeHeadless
npm run build -- --configuration production
```

Expected: install, tests, and production build succeed without a live backend.

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/ci.yml
git commit -m "ci: verify frontend tests and production build"
```

### Task 3: Rewrite backend documentation against the verified API

**Files:**
- Rewrite: `BackEnd/WebAppBookLibrary/README.md`
- Modify if necessary: `BackEnd/WebAppBookLibrary/.env.example`

**Interfaces:**
- Consumes: final endpoints, policies, test commands, and limitations from the backend plan.
- Produces: accurate backend setup and API reference.

- [ ] **Step 1: Create a documentation assertion checklist**

Before editing, verify every documented route against controller attributes:

Run: `rg -n "\[Route|\[Http" WebAppBookLibrary/Controllers`

Expected: output is the source of truth for the API table.

- [ ] **Step 2: Rewrite the README**

Include these concrete sections:

- portfolio overview and architecture;
- .NET 8 and MongoDB prerequisites;
- safe `.env` setup using `.env.example` names only;
- restore, run, build, and test commands;
- `/api` endpoint table copied from verified controller routes;
- the approved role matrix;
- password hashing, authorization, atomic reservation, audit logging, rate limiting, and safe-error behavior;
- Swagger and health-check usage;
- explicit limitations: no refresh tokens, role-management UI, email flows, or security certification; and
- CI behavior.

Remove Entity Framework migration instructions because persistent domain data uses MongoDB and no migrations exist.

- [ ] **Step 3: Scan for stale or inflated claims**

Run:

```powershell
rg -n "refresh|profile management|real-time|database migrations|penetration|enterprise|perfect|insecure API" README.md
```

Expected: only explicit limitation wording may match; no feature claim remains.

- [ ] **Step 4: Verify commands and commit**

Run: `dotnet test WebAppBookLibrary.sln --configuration Release`

```powershell
git add README.md .env.example
git commit -m "docs: present the verified backend architecture"
```

### Task 4: Rewrite frontend documentation against the verified SPA

**Files:**
- Rewrite: `FrontEnd/Book-Library-Client/README.md`
- Modify if necessary: `FrontEnd/Book-Library-Client/.env.example`

**Interfaces:**
- Consumes: final routes, session behavior, dashboard semantics, test commands, and limitations from the frontend plan.
- Produces: accurate portfolio-facing frontend documentation.

- [ ] **Step 1: Verify the route and script source of truth**

Run:

```powershell
rg -n "path:" src/app/app.routes.ts src/app/features -g "*routing.module.ts"
Get-Content -Raw package.json
```

Expected: these outputs define the documented routes and npm commands.

- [ ] **Step 2: Rewrite the README**

Include these concrete sections:

- screenshots placeholder guidance without embedding a nonexistent image;
- Angular architecture and feature map;
- role-dependent navigation and session-expiry behavior;
- local proxy setup using `/api`;
- `npm ci`, development, test, and production-build commands;
- dashboard probe definitions, four statuses, exact score formula, and the “not a penetration test” limitation;
- backend dependency and startup order;
- CI behavior; and
- concise portfolio talking points tied to actual code.

Remove claims about profiles, refresh tokens, real-time updates, OnPush usage, Docker files, runtime `.env` loading in Angular, and server-side book search unless they exist after implementation.

- [ ] **Step 3: Scan for stale or inflated claims**

Run:

```powershell
rg -n "refresh token|profile management|real-time|OnPush|Dockerfile|penetration test|perfect|insecure API" README.md
```

Expected: only explicit limitation wording may match.

- [ ] **Step 4: Verify commands and commit**

Run:

```powershell
npm test -- --watch=false --browsers=ChromeHeadless
npm run build -- --configuration production
```

```powershell
git add README.md .env.example
git commit -m "docs: present the verified Angular application"
```

### Task 5: Cross-repository acceptance verification

**Files:**
- Modify only files required by a failed acceptance criterion; behavioral corrections require a failing regression test first.

**Interfaces:**
- Consumes: completed backend, frontend, CI, and documentation plans.
- Produces: final evidence for all ten acceptance criteria in the design specification.

- [ ] **Step 1: Verify backend**

Run from `BackEnd/WebAppBookLibrary`:

```powershell
dotnet restore WebAppBookLibrary.sln
dotnet test WebAppBookLibrary.sln --configuration Release --no-restore
dotnet build WebAppBookLibrary.sln --configuration Release --no-restore
git status --short
```

Expected: restore/build/tests succeed and status contains no generated or secret files.

- [ ] **Step 2: Verify frontend**

Run from `FrontEnd/Book-Library-Client`:

```powershell
npm ci
npm test -- --watch=false --browsers=ChromeHeadless
npm run build -- --configuration production
git status --short
```

Expected: clean install/tests/build succeed and status contains no generated or secret files.

- [ ] **Step 3: Verify security invariants statically**

Run from the workspace root:

```powershell
rg -n "Role\s*=\s*request\.Role|availableRoles|Math\.random|insecureApiUrl|detail\s*=\s*error\.Error\.Message" BackEnd FrontEnd
```

Expected: no matches.

- [ ] **Step 4: Verify repository hygiene**

Run `git status --short` in each repository and `git check-ignore -v` for local `.env` and generated directories.

Expected: secrets and generated outputs are ignored; only intentional plan/document changes remain.

- [ ] **Step 5: Record final handoff**

Report exact test counts, build results, commits in each repository, remaining non-goals, and the commands a reviewer can run. Do not claim completion unless every required command succeeded.
