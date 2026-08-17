using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly UserService _userService;
        private readonly Logservice _logService;

        public AuthController(IConfiguration configuration, UserService userService, Logservice logService)
        {
            _configuration = configuration;
            _userService = userService;
            _logService = logService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                await _logService.LogAsync("INFORMATION", $"Intento de registro para usuario: {request.Username}");

                if (string.IsNullOrWhiteSpace(request.Username) ||
                    string.IsNullOrWhiteSpace(request.Password) ||
                    string.IsNullOrWhiteSpace(request.Role))
                {
                    return BadRequest(new { error = "Username, Password and Role are required." });
                }

                if (!EmailValidator.IsValid(request.Email))
                    return BadRequest(new { error = "Invalid email format." });

                if (!PasswordValidator.IsValid(request.Password))
                    return BadRequest(new
                    {
                        error = "Password must be at least 5 characters long, contain at least one uppercase letter, one lowercase letter, and one number."
                    });

                var user = new User
                {
                    Username = request.Username,
                    Email = request.Email,
                    Role = request.Role
                };

                var createdUser = await _userService.CreateUserAsync(user, request.Password);
                if (createdUser == null)
                    return BadRequest(new { error = "User already exists." });

                await _logService.LogAsync("INFORMATION", $"Usuario registrado exitosamente: {request.Username}");
                return Ok(new { message = "User registered successfully.", data = new { username = user.Username, email = user.Email, role = user.Role } });
            }
            catch (Exception ex)
            {
                await _logService.LogAsync("ERROR", $"Error durante el registro de usuario: {request.Username}", ex);
                return StatusCode(500, new { error = "Internal server error." });
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                    return BadRequest(new { error = "Username and password are required." });

                var user = await _userService.GetUserByUserNameAsync(request.Username);

                if (user == null || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
                    return Unauthorized(new { error = "Invalid username or password." });

                var jwtSettings = _configuration.GetSection("Jwt");
                var jwtKey = jwtSettings.GetValue<string>("Key");
                var jwtIssuer = jwtSettings.GetValue<string>("Issuer");
                var jwtAudience = jwtSettings.GetValue<string>("Audience");

                if (string.IsNullOrWhiteSpace(jwtKey))
                    throw new InvalidOperationException("JWT key is missing from configuration.");

                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
                var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

                var claims = new[]
                {
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };

                var expiration = DateTime.UtcNow.AddHours(1);
                var token = new JwtSecurityToken(
                    issuer: jwtIssuer,
                    audience: jwtAudience,
                    claims: claims,
                    expires: expiration,
                    signingCredentials: creds
                );

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
                await _logService.LogAsync("ERROR", $"Error durante el login de usuario: {request.Username}", ex);
                return StatusCode(500, new { error = "Internal server error." });
            }
        }

    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "User"; // Default role
    }

    public class RegisterRequest : LoginRequest
    {
        public string Email { get; set; } = string.Empty;
    }
}
