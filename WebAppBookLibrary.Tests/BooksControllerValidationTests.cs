using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Controllers;

namespace WebAppBookLibrary.Tests;

public class BooksControllerValidationTests
{
    [Fact]
    public async Task GetById_rejects_invalid_object_id_before_Mongo()
    {
        var controller = new BooksController(null!, null!);

        var result = await controller.GetById("not-an-object-id");

        AssertBadRequestProblem(result);
    }

    [Fact]
    public async Task Update_rejects_invalid_object_id_before_Mongo()
    {
        var controller = new BooksController(null!, null!);
        var request = new UpsertBookRequest
        {
            Title = "Title",
            Author = "Author"
        };

        var result = await controller.Update("not-an-object-id", request);

        AssertBadRequestProblem(result);
    }

    [Fact]
    public async Task Delete_rejects_invalid_object_id_before_Mongo()
    {
        var controller = new BooksController(null!, null!);

        var result = await controller.Delete("not-an-object-id");

        AssertBadRequestProblem(result);
    }

    private static void AssertBadRequestProblem(IActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problem.Status);
        Assert.Null(problem.Detail);
    }
}
