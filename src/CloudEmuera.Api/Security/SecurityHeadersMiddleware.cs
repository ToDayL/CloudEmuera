namespace CloudEmuera.Api.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context)
    {
        IWebHostEnvironment environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        string developmentStyle = environment.IsDevelopment() ? " 'unsafe-inline'" : string.Empty;
        context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
        context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
        context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=(), usb=()");
        context.Response.Headers.TryAdd("Content-Security-Policy", $"default-src 'self'; script-src 'self'; style-src 'self'{developmentStyle}; style-src-attr 'unsafe-inline'; connect-src 'self' ws: wss:; img-src 'self' blob:; media-src 'self'; font-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'none'; form-action 'self'; frame-ancestors 'none'");
        await next(context).ConfigureAwait(false);
    }
}
