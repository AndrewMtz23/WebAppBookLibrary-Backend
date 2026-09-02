using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Auth;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly JwtOptions _jwtOptions;
    private readonly UserService _userService;
    private readonly Logservice _logService;

    public AuthController(IOptions<JwtOptions> jwtOptions, UserService userService, Logservice logService)
    {
        _jwtOptions = jwtOptions.Value;
        _userService = userService;
        _logService = logService;
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            if (request is null)
                return BadRequest(new { error = "Registration details are required." });

            await _logService.LogAsync("INFORMATION", $"Intento de registro para usuario: {request.Username}");

            var result = await _userService.CreateUserAsync(request);
            if (!result.Success)
            {
                if (result.ErrorCode == UserCreationErrorCodes.DuplicateUser)
                    return Conflict(new { error = "User already exists." });

                return BadRequest(new { error = "Invalid registration details." });
            }

            var createdUser = result.User!;
            await _logService.LogAsync("INFORMATION", $"Usuario registrado exitosamente: {request.Username}");
            return Ok(new
            {
                message = "User registered successfully.",
                data = new
                {
                    username = createdUser.Username,
                    email = createdUser.Email,
                    role = createdUser.Role
                }
            });
        }
        catch (Exception ex)
        {
            await _logService.LogAsync("ERROR", $"Error durante el registro de usuario: {request?.Username}", ex);
            return StatusCode(500, new { error = "Internal server error." });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { error = "Username and password are required." });

            var user = await _userService.GetUserByUserNameAsync(request.Username);

            if (user == null || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
                return Unauthorized(new { error = "Invalid username or password." });

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, request.Username),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var token = new JwtSecurityToken(
                issuer: _jwtOptions.Issuer,
                audience: _jwtOptions.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds);

            await _logService.LogAsync("INFORMATION", $"Login exitoso para usuario: {request.Username}");

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                user = new
                {
                    id = user.Id,
                    username = user.Username,
                    email = user.Email,
                    role = user.Role
                }
            });
        }
        catch (Exception ex)
        {
            await _logService.LogAsync("ERROR", $"Error durante el login de usuario: {request?.Username}", ex);
            return StatusCode(500, new { error = "Internal server error." });
        }
    }
}
