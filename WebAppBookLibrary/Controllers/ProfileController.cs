using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
using System.Security.Claims;
using WebAppBookLibrary.Contracts.Profile;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize]
public sealed class ProfileController(ProfileService service) : ControllerBase
{
    [HttpPut("me")]
    public async Task<IActionResult> Update(UpdateProfileRequest request, CancellationToken token)
    {
        var result = await service.UpdateAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", User.Identity?.Name ?? "", request, token);
        return result.Status switch
        {
            200 => Ok(result.Profile),
            400 => ApiProblemFactory.Result(400, "Revisa el nombre, el correo y la URL HTTPS de la foto."),
            401 => ApiProblemFactory.Result(401, "La sesión ya no es válida."),
            _ => ApiProblemFactory.Result(409, "El correo ya está en uso o tu cuenta cambió. Recarga el perfil antes de reintentar.")
        };
    }
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var profile = await service.GetAsync(User.Identity?.Name ?? string.Empty);
        return profile is null
            ? ApiProblemFactory.Result(StatusCodes.Status403Forbidden, "Profile is not permitted")
            : Ok(profile);
    }
}
