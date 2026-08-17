using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace WebAppBookLibrary.Services
{
    // Clase base personalizada que usa TimeProvider
    public abstract class ModernAuthenticationHandler<TOptions> : IAuthenticationHandler
        where TOptions : AuthenticationSchemeOptions, new()
    {
        protected AuthenticationScheme Scheme { get; private set; } = null!;
        protected TOptions Options { get; private set; } = null!;
        protected HttpContext Context { get; private set; } = null!;
        protected ILogger Logger { get; }
        protected UrlEncoder UrlEncoder { get; }
        protected TimeProvider TimeProvider { get; }

        private readonly IOptionsMonitor<TOptions> _optionsMonitor;

        protected ModernAuthenticationHandler(
            IOptionsMonitor<TOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            TimeProvider timeProvider)
        {
            _optionsMonitor = options;
            Logger = logger.CreateLogger(GetType());
            UrlEncoder = encoder;
            TimeProvider = timeProvider;
        }

        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            Scheme = scheme;
            Context = context;
            Options = _optionsMonitor.Get(scheme.Name);
            return Task.CompletedTask;
        }

        public async Task<AuthenticateResult> AuthenticateAsync()
        {
            try
            {
                return await HandleAuthenticateAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during authentication");
                return AuthenticateResult.Fail(ex);
            }
        }

        public Task ChallengeAsync(AuthenticationProperties? properties)
        {
            return HandleChallengeAsync(properties);
        }

        public Task ForbidAsync(AuthenticationProperties? properties)
        {
            return HandleForbiddenAsync(properties);
        }

        protected abstract Task<AuthenticateResult> HandleAuthenticateAsync();

        protected virtual Task HandleChallengeAsync(AuthenticationProperties? properties)
        {
            Context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }

        protected virtual Task HandleForbiddenAsync(AuthenticationProperties? properties)
        {
            Context.Response.StatusCode = 403;
            return Task.CompletedTask;
        }

        protected DateTimeOffset GetUtcNow() => TimeProvider.GetUtcNow();
    }

    // Tu handler dummy usando la clase base moderna
    public class DummyAuthHandler : ModernAuthenticationHandler<AuthenticationSchemeOptions>
    {
        public DummyAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            TimeProvider timeProvider)
            : base(options, logger, encoder, timeProvider)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Task.FromResult(AuthenticateResult.Fail("Not authenticated"));
        }
    }
}