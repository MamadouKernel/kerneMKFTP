namespace KernelMK.Web.Security;

/// <summary>Browser protections compatible with Blazor's scripts, styles and WebSocket connection.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.TryAdd("Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=(), usb=()");
        headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");
        // Restrict framing and document base URLs without blocking Blazor's inline scripts.
        headers.TryAdd("Content-Security-Policy", "frame-ancestors 'self'; base-uri 'self'; object-src 'none'");
        return next(context);
    }
}