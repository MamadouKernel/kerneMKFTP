using KernelMK.Web.Security;
using Microsoft.AspNetCore.Http;

namespace KernelMK.Tests.Web;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task BrowserProtectionsApplyToRedirectsAndErrors()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(http =>
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("SAMEORIGIN", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("camera=(), geolocation=(), microphone=(), payment=(), usb=()", context.Response.Headers["Permissions-Policy"].ToString());
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"].ToString());
        var policy = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("http:", policy);
        Assert.DoesNotContain("https:", policy);
    }
}