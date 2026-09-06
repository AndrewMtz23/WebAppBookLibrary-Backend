using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize(Policy = PolicyNames.BorrowBooks)]
public sealed class ProfileController(ProfileService service) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var profile = await service.GetAsync(User.Identity?.Name ?? string.Empty);
        return profile is null
            ? ApiProblemFactory.Result(StatusCodes.Status403Forbidden, "Profile is not permitted")
            : Ok(profile);
    }
}
