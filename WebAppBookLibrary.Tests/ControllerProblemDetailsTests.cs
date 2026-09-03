using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Auth;
using WebAppBookLibrary.Controllers;

namespace WebAppBookLibrary.Tests;

public class ControllerProblemDetailsTests
{
    [Fact]
    public async Task Registration_validation_failure_returns_problem_details()
    {
        var controller = CreateAuthController();

        var result = await controller.Register(null!);

        AssertProblem(result, 400);
    }

    [Fact]
    public async Task Login_validation_failure_returns_problem_details()
    {
        var controller = CreateAuthController();

        var result = await controller.Login(new LoginRequest("", ""));

        AssertProblem(result, 400);
    }

    private static AuthController CreateAuthController()
    {
        return new AuthController(
            Options.Create(new JwtOptions
            {
                Key = "a-strong-test-key-with-at-least-32-characters",
                Issuer = "issuer",
                Audience = "audience"
            }),
            null!,
            null!);
    }

    private static void AssertProblem(IActionResult result, int statusCode)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(statusCode, problem.Status);
        Assert.Null(problem.Detail);
    }
}
