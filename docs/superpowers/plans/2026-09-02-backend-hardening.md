# Backend Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ASP.NET Core API enforce safe registration, canonical authorization, consistent JWT settings, honest health reporting, and concurrency-safe loans.

**Architecture:** Controllers expose DTOs under `/api`, policies express permissions, and service classes own business decisions. Small persistence interfaces isolate MongoDB so registration and loan behavior can be tested deterministically without Atlas credentials.

**Tech Stack:** .NET 8, ASP.NET Core, MongoDB.Driver 3.4.1, xUnit, Moq, Swagger/OpenAPI

**Spec:** `docs/superpowers/specs/2026-09-02-professional-portfolio-hardening-design.md`

## Global Constraints

- Keep .NET 8 and MongoDB; add no external hosted service.
- Canonical roles are exactly `user`, `librarian`, and `admin`.
- Public registration always creates `user`; request bodies cannot assign privileged roles.
- Dates are UTC and loan due dates remain 14 days after creation.
- Tests must run without MongoDB credentials or network access.
- API failures must not expose exception messages, stack traces, credentials, or JWTs.

---

### Task 1: Add the backend test boundary and role vocabulary

**Files:**
- Create: `WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj`
- Create: `WebAppBookLibrary.Tests/RoleNamesTests.cs`
- Create: `WebAppBookLibrary/Security/RoleNames.cs`
- Create: `WebAppBookLibrary/Security/PolicyNames.cs`
- Modify: `WebAppBookLibrary.sln`

**Interfaces:**
- Produces: `RoleNames.User`, `RoleNames.Librarian`, `RoleNames.Admin`; `PolicyNames.BorrowBooks`, `PolicyNames.ManageBooks`, `PolicyNames.DeleteBooks`, `PolicyNames.ViewAllLoans`, `PolicyNames.ViewAudit`.

- [ ] **Step 1: Create the xUnit project and failing vocabulary test**

```csharp
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Tests;

public class RoleNamesTests
{
    [Fact]
    public void Roles_are_canonical_lowercase_values()
    {
        Assert.Equal("user", RoleNames.User);
        Assert.Equal("librarian", RoleNames.Librarian);
        Assert.Equal("admin", RoleNames.Admin);
    }
}
```

The test project targets `net8.0`, references the API project, and uses `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, and `Moq`.

- [ ] **Step 2: Run the test and verify RED**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --no-restore`

Expected: compilation fails because `WebAppBookLibrary.Security.RoleNames` does not exist.

- [ ] **Step 3: Add canonical role and policy constants**

```csharp
namespace WebAppBookLibrary.Security;

public static class RoleNames
{
    public const string User = "user";
    public const string Librarian = "librarian";
    public const string Admin = "admin";
}
```

Add the five policy string constants listed in **Interfaces** to `PolicyNames.cs`.

- [ ] **Step 4: Add the project to the solution and verify GREEN**

Run: `dotnet sln WebAppBookLibrary.sln add WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj`

Run: `dotnet test WebAppBookLibrary.sln`

Expected: `Roles_are_canonical_lowercase_values` passes.

- [ ] **Step 5: Commit**

```powershell
git add WebAppBookLibrary.sln WebAppBookLibrary.Tests WebAppBookLibrary/Security
git commit -m "test: establish backend security vocabulary"
```

### Task 2: Make public registration safe and deterministic

**Files:**
- Create: `WebAppBookLibrary/Contracts/Auth/RegisterRequest.cs`
- Create: `WebAppBookLibrary/Contracts/Auth/LoginRequest.cs`
- Create: `WebAppBookLibrary/Services/IUserStore.cs`
- Create: `WebAppBookLibrary/Services/MongoUserStore.cs`
- Create: `WebAppBookLibrary.Tests/UserServiceTests.cs`
- Modify: `WebAppBookLibrary/Services/UserService.cs`
- Modify: `WebAppBookLibrary/Controllers/AuthController.cs`
- Modify: `WebAppBookLibrary/Models/User.cs`
- Modify: `WebAppBookLibrary/Program.cs`

**Interfaces:**
- Consumes: `RoleNames.User` from Task 1.
- Produces: `RegisterRequest(string Username, string Password, string Email)` with no role; `IUserStore.FindByUsernameOrEmailAsync` and `IUserStore.InsertAsync`; `UserService.CreateUserAsync(RegisterRequest)`.

- [ ] **Step 1: Write failing registration tests**

```csharp
[Fact]
public async Task CreateUser_assigns_user_role()
{
    var store = new Mock<IUserStore>();
    store.Setup(x => x.FindByUsernameOrEmailAsync("ana", "ana@example.com"))
         .ReturnsAsync((User?)null);
    User? inserted = null;
    store.Setup(x => x.InsertAsync(It.IsAny<User>()))
         .Callback<User>(u => inserted = u)
         .Returns(Task.CompletedTask);

    var service = new UserService(store.Object);
    var result = await service.CreateUserAsync(
        new RegisterRequest("ana", "Secure1", "ana@example.com"));

    Assert.True(result.Success);
    Assert.Equal(RoleNames.User, inserted!.Role);
}

[Fact]
public void RegisterRequest_has_no_role_property()
{
    Assert.DoesNotContain(typeof(RegisterRequest).GetProperties(), p => p.Name == "Role");
}
```

Add cases for duplicate username/email, invalid email, and a password lacking uppercase, lowercase, or number.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --filter UserServiceTests`

Expected: compilation fails because the request, store, and new service API do not exist.

- [ ] **Step 3: Implement the registration boundary**

Create immutable request DTOs. Move Mongo user queries into `MongoUserStore`. Make `UserService` validate the request, check username and email together, hash the password, assign `RoleNames.User`, and return a typed result such as `UserCreationResult(bool Success, string ErrorCode, User? User)`.

Update `AuthController.Register` to consume the role-free DTO and map validation failures to `400` and duplicates to `409`. Never bind or echo a requested role.

- [ ] **Step 4: Verify GREEN and controller surface**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --filter UserServiceTests`

Run: `dotnet build WebAppBookLibrary.sln`

Expected: all registration cases pass and the API builds.

- [ ] **Step 5: Commit**

```powershell
git add WebAppBookLibrary WebAppBookLibrary.Tests
git commit -m "fix: prevent role escalation during registration"
```

### Task 3: Unify JWT settings, policies, rate limits, errors, and API routes

**Files:**
- Create: `WebAppBookLibrary/Configuration/JwtOptions.cs`
- Create: `WebAppBookLibrary/Controllers/HealthController.cs`
- Create: `WebAppBookLibrary/Contracts/Books/UpsertBookRequest.cs`
- Create: `WebAppBookLibrary/Contracts/Loans/CreateLoanRequest.cs`
- Create: `WebAppBookLibrary.Tests/JwtOptionsTests.cs`
- Create: `WebAppBookLibrary.Tests/AuthorizationPolicyTests.cs`
- Create: `WebAppBookLibrary.Tests/RequestContractTests.cs`
- Modify: `WebAppBookLibrary/Program.cs`
- Modify: `WebAppBookLibrary/Controllers/AuthController.cs`
- Modify: `WebAppBookLibrary/Controllers/BooksController.cs`
- Modify: `WebAppBookLibrary/Controllers/LoansController.cs`
- Modify: `WebAppBookLibrary/Controllers/LogController.cs`
- Modify: `WebAppBookLibrary/Services/Logservice.cs`

**Interfaces:**
- Consumes: role and policy constants from Task 1.
- Produces: validated `JwtOptions`; `/api/health`; named authorization policies; named `auth` rate-limit policy; `/api/[controller]` route convention.

- [ ] **Step 1: Write failing configuration and policy tests**

```csharp
[Fact]
public void JwtOptions_rejects_short_signing_key()
{
    var options = new JwtOptions { Key = "short", Issuer = "issuer", Audience = "audience" };
    var results = new List<ValidationResult>();

    Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
}
```

Add policy tests that build the service provider and assert `BorrowBooks` accepts `user` only, `ManageBooks` accepts `librarian` and `admin`, and `ViewAudit` accepts `admin` only.

Add request-contract tests proving book input does not expose `Id` or `IsAvailable` and loan input exposes only `BookId`:

```csharp
[Fact]
public void Book_input_does_not_allow_identity_or_availability_assignment()
{
    var names = typeof(UpsertBookRequest).GetProperties().Select(p => p.Name).ToArray();
    Assert.DoesNotContain("Id", names);
    Assert.DoesNotContain("IsAvailable", names);
}
```

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --filter "JwtOptionsTests|AuthorizationPolicyTests"`

Expected: tests fail because validated options and named policies are absent.

- [ ] **Step 3: Implement one configuration path and authorization policies**

Bind `JwtOptions` once after mapping `JWT_KEY`, `JWT_ISSUER`, and `JWT_AUDIENCE` into configuration. Apply `ValidateDataAnnotations()` and `ValidateOnStart()`. Inject `IOptions<JwtOptions>` into token creation so generation and validation use the same object.

Register policies with `RequireRole` using canonical values. Replace duplicated role strings on controllers with policies. Remove `AllowAnonymous` from book reads. Apply `EnableRateLimiting("auth")` to login and registration with a fixed-window limit of five requests per minute per remote IP.

Change controller routes to `api/[controller]`. Add `AddProblemDetails`, a production-safe exception handler, Swagger bearer authentication, and `GET /api/health` returning `{ status = "healthy", timestampUtc }` without checking or exposing credentials.

Replace direct `Book` and controller-nested loan input binding with `UpsertBookRequest` and `CreateLoanRequest`. Validate title/author length, optional year range, genre length, and `BookId` before mapping DTOs into domain models. Book creation always sets its identifier server-side and starts available.

Populate audit `Method` and `IP` fields in `Logservice`.

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --filter "JwtOptionsTests|AuthorizationPolicyTests"`

Run: `dotnet build WebAppBookLibrary.sln`

Expected: policy/configuration tests and build pass with no compiler warnings.

- [ ] **Step 5: Commit**

```powershell
git add WebAppBookLibrary WebAppBookLibrary.Tests
git commit -m "feat: harden API authentication and authorization"
```

### Task 4: Make loan creation and return concurrency-safe

**Files:**
- Create: `WebAppBookLibrary/Services/ILoanStore.cs`
- Create: `WebAppBookLibrary/Services/MongoLoanStore.cs`
- Create: `WebAppBookLibrary/Contracts/Loans/LoanOperationResult.cs`
- Create: `WebAppBookLibrary.Tests/LoanServiceTests.cs`
- Modify: `WebAppBookLibrary/Services/LoanService.cs`
- Modify: `WebAppBookLibrary/Controllers/LoansController.cs`
- Modify: `WebAppBookLibrary/Program.cs`

**Interfaces:**
- Produces: `ILoanStore.ReserveAvailableBookAsync`, `RestoreBookAvailabilityAsync`, `FindActiveUserAsync`, `InsertLoanAsync`, `FindActiveLoanAsync`, `MarkReturnedAsync`; `LoanOperationResult` with stable error codes.
- Consumes: canonical roles and policies from Task 1.

- [ ] **Step 1: Write failing loan behavior tests**

```csharp
[Fact]
public async Task CreateLoan_stops_when_atomic_reservation_loses()
{
    var store = new Mock<ILoanStore>();
    store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana", "user"));
    store.Setup(x => x.ReserveAvailableBookAsync("b1")).ReturnsAsync((Book?)null);
    var service = CreateService(store);

    var result = await service.CreateLoanAsync("b1", "ana");

    Assert.False(result.Success);
    Assert.Equal("book_unavailable", result.ErrorCode);
    store.Verify(x => x.InsertLoanAsync(It.IsAny<Loan>()), Times.Never);
}
```

Add tests proving that insertion failure restores the book, a user cannot return another user's loan, librarian/admin behavior follows the role matrix, and a repeated return reports an idempotent non-error result without updating twice.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --filter LoanServiceTests`

Expected: compilation fails because `ILoanStore` and the revised behavior do not exist.

- [ ] **Step 3: Implement the Mongo store and minimal orchestration**

Implement reservation with `FindOneAndUpdateAsync` using the filter `Id == bookId && IsAvailable == true` and update `IsAvailable = false`, returning the pre-update or post-update book. Validate identifiers with `ObjectId.TryParse` before database calls.

In `LoanService`, find the active user, reserve once, insert the loan, and restore availability in the catch path before returning `loan_persistence_failed`. Return validation receives the caller role, checks ownership for `user`, allows staff according to the approved matrix, and performs a conditional `IsReturned == false` update. A missing active loan after an already-completed return maps to an idempotent result rather than changing the book twice.

- [ ] **Step 4: Verify GREEN and regression suite**

Run: `dotnet test WebAppBookLibrary.sln`

Run: `dotnet build WebAppBookLibrary.sln --configuration Release`

Expected: all loan tests and existing backend tests pass; Release build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add WebAppBookLibrary WebAppBookLibrary.Tests
git commit -m "fix: make lending operations concurrency safe"
```

### Task 5: Final backend verification

**Files:**
- Modify only files required by a failing verification; every behavioral correction requires a new failing regression test first.

**Interfaces:**
- Consumes: all backend deliverables from Tasks 1-4.
- Produces: a clean backend test and Release-build baseline for the frontend and CI plans.

- [ ] **Step 1: Restore from declared dependencies**

Run: `dotnet restore WebAppBookLibrary.sln --force-evaluate`

Expected: restore succeeds without credentials.

- [ ] **Step 2: Run the complete test suite**

Run: `dotnet test WebAppBookLibrary.sln --configuration Release --no-restore`

Expected: all tests pass.

- [ ] **Step 3: Run the Release build**

Run: `dotnet build WebAppBookLibrary.sln --configuration Release --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 4: Inspect repository state**

Run: `git status --short`

Expected: no generated, secret, or unrelated files are staged.

- [ ] **Step 5: Commit verification-only adjustments if any**

```powershell
git add WebAppBookLibrary WebAppBookLibrary.Tests
git commit -m "test: complete backend hardening coverage"
```
