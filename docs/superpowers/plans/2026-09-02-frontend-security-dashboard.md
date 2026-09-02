# Frontend Security Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a clean Angular build, enforce valid local sessions, use typed `/api` contracts, and replace the fabricated comparison with an honest security-health dashboard.

**Architecture:** Angular services use one relative `/api` base URL and typed response envelopes. Authentication utilities own JWT expiry, the interceptor owns `401` handling, and a probe service using the raw HTTP backend records observable anonymous API behavior without the JWT interceptor.

**Tech Stack:** Angular 17, TypeScript 5.2, RxJS 7.8, Angular Material, Jasmine/Karma, Highcharts

**Spec:** `docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md`

## Global Constraints

- Keep Angular 17 and the existing Material visual language.
- Use `/api` as the only frontend API base path.
- Registration requests contain username, password, and email only.
- Security results contain no random values, forced classifications, artificial delays, or claims that were not observed.
- Tests must be deterministic, noninteractive in CI, and must not call a live API.
- Do not add a JWT-decoding dependency for the single expiry claim.

---

### Task 1: Repair the clean frontend toolchain baseline

**Files:**
- Modify: `FrontEnd/Book-Library-Client/tsconfig.app.json`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/config.service.ts`
- Modify: `FrontEnd/Book-Library-Client/src/environments/environment.ts`
- Modify: `FrontEnd/Book-Library-Client/src/environments/environment.prod.ts`
- Modify: `FrontEnd/Book-Library-Client/proxy.conf.json`
- Delete: `FrontEnd/Book-Library-Client/public/config.json`
- Delete: `FrontEnd/Book-Library-Client/src/environments/enviroanalysis.ts`

**Interfaces:**
- Produces: `environment.apiUrl === '/api'` in development and production; proxy forwards `/api` to `https://localhost:7086`.

- [ ] **Step 1: Reproduce the clean-install failures**

Run from `FrontEnd/Book-Library-Client`:

```powershell
npm ci
npm run build
```

Expected before changes: build reports invalid `ignoreDeprecations` and the bad `../../environments/environment` import. Record any dependency-cache error separately; `npm ci` must remove that environmental cause.

- [ ] **Step 2: Apply the minimal configuration correction**

Remove `ignoreDeprecations: "6.0"`. Remove the unused `ConfigService` and runtime `config.json` path rather than maintaining two sources of truth. Set both environment files to:

```typescript
export const environment = {
  production: false, // true in environment.prod.ts
  apiUrl: '/api'
};
```

Change the proxy key from `/api/*` to `/api` while retaining the HTTPS localhost target.

- [ ] **Step 3: Verify the configuration failure is gone**

Run: `npm run build`

Expected: no TypeScript configuration or `ConfigService` import error. Feature-level compilation failures, if any, become the RED baseline for later tasks.

- [ ] **Step 4: Commit**

```powershell
git add tsconfig.app.json proxy.conf.json src/environments src/app/core/services/config.service.ts public/config.json
git commit -m "fix: restore deterministic Angular configuration"
```

### Task 2: Introduce typed API contracts and canonical routes

**Files:**
- Create: `FrontEnd/Book-Library-Client/src/app/shared/models/api-response.model.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/shared/constant/shared-constants.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/shared/models/auth-request.model.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/auth.service.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/book.service.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/loan.service.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/log.service.ts`
- Modify: feature consumers under `FrontEnd/Book-Library-Client/src/app/features/`
- Create: `FrontEnd/Book-Library-Client/src/app/core/services/book.service.spec.ts` behavioral cases

**Interfaces:**
- Produces: `ApiResponse<T> { message: string; data: T }`; role-free `RegisterRequest`; service URLs relative to `/api`.

- [ ] **Step 1: Replace the placeholder book-service test with a failing contract test**

```typescript
it('requests the typed books endpoint', () => {
  service.getAll().subscribe(response => expect(response.data).toEqual([]));

  const request = httpTestingController.expectOne('/api/books');
  expect(request.request.method).toBe('GET');
  request.flush({ message: 'Books retrieved', data: [] });
});
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `npm test -- --watch=false --browsers=ChromeHeadless --include src/app/core/services/book.service.spec.ts`

Expected: test fails because the service type/URL does not match the `/api` envelope.

- [ ] **Step 3: Implement shared response types and update consumers**

```typescript
export interface ApiResponse<T> {
  message: string;
  data: T;
}
```

Type each service method with its actual backend response. Remove component fallbacks such as `res.data ?? res`. Remove non-existent client methods (`getByLevel`, full loan update, or get-by-id) unless an implemented endpoint consumes them. Update auth URLs to lowercase `/api/auth/login` and `/api/auth/register`.

- [ ] **Step 4: Verify GREEN and compile contracts**

Run the focused service test, then run: `npm run build`

Expected: service test passes and no component relies on an untyped envelope fallback.

- [ ] **Step 5: Commit**

```powershell
git add src/app/shared src/app/core/services src/app/features
git commit -m "refactor: align frontend with typed API contracts"
```

### Task 3: Enforce token expiry, `401` logout, and safe registration UI

**Files:**
- Create: `FrontEnd/Book-Library-Client/src/app/core/auth/jwt-token.ts`
- Create: `FrontEnd/Book-Library-Client/src/app/core/auth/jwt-token.spec.ts`
- Create: `FrontEnd/Book-Library-Client/src/app/core/http/api-error.ts`
- Create: `FrontEnd/Book-Library-Client/src/app/core/http/api-error.spec.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/auth.service.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/services/auth.service.spec.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/core/interceptors/auth.interceptor.ts`
- Create: `FrontEnd/Book-Library-Client/src/app/core/interceptors/auth.interceptor.spec.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/features/auth/register/register.component.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/features/auth/register/register.component.html`
- Modify: `FrontEnd/Book-Library-Client/src/app/features/auth/register/register.component.spec.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/app.component.ts`

**Interfaces:**
- Produces: `getTokenExpiration(token: string): number | null`; `isTokenExpired(token, nowSeconds)`; `ApiError { status: number; message: string }`; `toApiError(HttpErrorResponse)`; idempotent `AuthService.logout()`; registration payload without role.

- [ ] **Step 1: Write failing JWT-expiry tests**

```typescript
it('treats a token whose exp is in the past as expired', () => {
  const token = tokenWithPayload({ exp: 100 });
  expect(isTokenExpired(token, 101)).toBeTrue();
});

it('treats malformed tokens as expired', () => {
  expect(isTokenExpired('not-a-jwt', 100)).toBeTrue();
});
```

Add an `AuthService` test proving `isLoggedIn()` clears an expired token, an interceptor test proving `401` calls logout and navigates to `/auth/login`, and a registration component test proving the submitted request has no `role` property.

Add error-mapping tests proving RFC problem details prefer `detail`, validation problems join their first messages, and an empty `500` response maps to a safe generic Spanish message rather than technical response text.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `npm test -- --watch=false --browsers=ChromeHeadless --include src/app/core/auth/jwt-token.spec.ts --include src/app/core/interceptors/auth.interceptor.spec.ts --include src/app/features/auth/register/register.component.spec.ts`

Expected: compilation or assertions fail because expiry and centralized `401` behavior do not exist and registration still sends a role.

- [ ] **Step 3: Implement minimal session behavior**

Decode only the JWT payload with URL-safe Base64 normalization and JSON parsing. Treat missing/invalid/non-numeric `exp` as expired. Make `isLoggedIn` validate expiry before returning true.

In the interceptor, add the bearer token, then `catchError`; map every `HttpErrorResponse` through `toApiError`, and on `401`, call logout and navigate once to `/auth/login` before rethrowing the typed error. Update feature error handlers to consume `ApiError.message` rather than duplicating status switches. Remove the registration role state, selector, and payload property. Remove temporary authentication `console.log` calls from services and `AppComponent`.

- [ ] **Step 4: Verify GREEN**

Run the focused tests, then: `npm test -- --watch=false --browsers=ChromeHeadless`

Expected: expiry, interceptor, and registration tests pass with the rest of the suite.

- [ ] **Step 5: Commit**

```powershell
git add src/app/core src/app/features/auth src/app/app.component.ts
git commit -m "fix: enforce safe frontend sessions and registration"
```

### Task 4: Replace fabricated security metrics with observable probe results

**Files:**
- Rewrite: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-analysis.service.ts`
- Rewrite: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.component.spec.ts`
- Create: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-analysis.service.spec.ts`

**Interfaces:**
- Produces: `ProbeStatus = 'passed' | 'failed' | 'inconclusive' | 'unavailable'`; `SecurityProbe`; `SecurityProbeResult`; `SecuritySummary`; `runSecurityAnalysis()`; `calculateSummary(results)`.

- [ ] **Step 1: Write failing mapping and scoring tests**

```typescript
it('marks a protected endpoint returning 401 as passed', () => {
  const result = service.classify(protectedProbe, 401, 25);
  expect(result.status).toBe('passed');
  expect(result.actualStatus).toBe(401);
});

it('excludes inconclusive and unavailable probes from the score', () => {
  const summary = service.calculateSummary([
    result('passed'), result('failed'), result('inconclusive'), result('unavailable')
  ]);
  expect(summary.score).toBe(50);
  expect(summary.conclusive).toBe(2);
});
```

Add cases for anonymous `200` on a protected endpoint (`failed`), expected `404` (`passed`), unexpected status (`inconclusive`), and network status `0` (`unavailable`).

- [ ] **Step 2: Run tests and verify RED**

Run: `npm test -- --watch=false --browsers=ChromeHeadless --include src/app/features/security-dashboard/security-analysis.service.spec.ts`

Expected: tests fail because the old secure/insecure and random-scoring API is still present.

- [ ] **Step 3: Implement deterministic probes**

Define probes for `GET /api/health` expecting `200`, protected book/loan/log routes expecting `401`, and `GET /api/users` expecting `404`. Construct a dedicated `HttpClient` from `HttpBackend` so anonymous probes bypass `AuthInterceptor`. Measure elapsed time with `Date.now`, preserve the real status, and return `forkJoin` results without `delay` or `Math.random`.

Calculate `score = passed / (passed + failed) * 100`, rounded to the nearest integer. Set `score` to `null` when the denominator is zero.

- [ ] **Step 4: Verify GREEN and scan for fabricated behavior**

Run the focused service test.

Run: `rg -n "Math.random|delay\(|secureApi|insecureApi|PERFECTA|vulnerability: true" src/app/features/security-dashboard`

Expected: tests pass and the scan returns no matches.

- [ ] **Step 5: Commit**

```powershell
git add src/app/features/security-dashboard/security-analysis.service.ts src/app/features/security-dashboard/security-analysis.service.spec.ts
git commit -m "fix: report observable security probe results"
```

### Task 5: Simplify and make the dashboard accessible

**Files:**
- Rewrite: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.component.ts`
- Rewrite: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.component.html`
- Rewrite: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.component.css`
- Delete: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.module.ts`
- Delete: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard-routing.module.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/features/security-dashboard/security-dashboard.component.spec.ts`
- Modify: `FrontEnd/Book-Library-Client/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `runSecurityAnalysis()` and `SecuritySummary` from Task 4.
- Produces: one standalone dashboard component with external HTML/CSS and admin-protected lazy route.

- [ ] **Step 1: Write failing component behavior tests**

```typescript
it('shows that the report is not a penetration test', () => {
  fixture.detectChanges();
  expect(fixture.nativeElement.textContent).toContain('no sustituye una auditoría ni una prueba de penetración');
});

it('renders unavailable without calling it a vulnerability', () => {
  component.results = [unavailableResult];
  fixture.detectChanges();
  expect(fixture.nativeElement.textContent).toContain('No disponible');
  expect(fixture.nativeElement.textContent).not.toContain('Vulnerable');
});
```

- [ ] **Step 2: Run the component test and verify RED**

Run: `npm test -- --watch=false --browsers=ChromeHeadless --include src/app/features/security-dashboard/security-dashboard.component.spec.ts`

Expected: assertions fail against the old comparison UI.

- [ ] **Step 3: Implement the single external-template component**

Render a header with last-run timestamp, numeric score only when non-null, counts by status, and one accessible card/table row per probe containing expectation, observed status, duration, and explanation. Use text plus icon/color so status is not communicated by color alone. Retain a single “Ejecutar análisis” action with loading state.

Delete the duplicate component disguised as a module and its unused routing module. Keep the existing lazy standalone route guarded by `AuthGuard` and `RoleGuard` for `admin`.

- [ ] **Step 4: Verify GREEN and production build**

Run the focused component test.

Run: `npm test -- --watch=false --browsers=ChromeHeadless`

Run: `npm run build -- --configuration production`

Expected: tests and build pass; no duplicate `SecurityDashboardComponent` declaration remains.

- [ ] **Step 5: Commit**

```powershell
git add src/app/features/security-dashboard src/app/app.routes.ts
git commit -m "refactor: present an honest security dashboard"
```

### Task 6: Final frontend verification and warning cleanup

**Files:**
- Modify: `FrontEnd/Book-Library-Client/src/app/features/loans/loansuser/loansuser.component.css`
- Modify only other files required by failing verification; behavioral fixes require a failing regression test first.

**Interfaces:**
- Consumes: all frontend deliverables from Tasks 1-5.
- Produces: clean test and production-build baselines for CI.

- [ ] **Step 1: Fix the known CSS compatibility warning**

Change `align-items: end` to `align-items: flex-end` in the loans stylesheet.

- [ ] **Step 2: Run the complete noninteractive test suite**

Run: `npm test -- --watch=false --browsers=ChromeHeadless`

Expected: all tests pass.

- [ ] **Step 3: Run a production build**

Run: `npm run build -- --configuration production`

Expected: build succeeds without application errors.

- [ ] **Step 4: Inspect repository state**

Run: `git status --short`

Expected: only intentional source changes are present; `node_modules`, build output, local environment files, and secrets are absent.

- [ ] **Step 5: Commit**

```powershell
git add src/app/features/loans/loansuser/loansuser.component.css
git commit -m "chore: complete frontend verification cleanup"
```
