using System.Security.Claims;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Web.Security;

public static class PushSubscriptionSecurity
{
    // Browser push services are the only intended destinations, never arbitrary HTTP hosts.
    public static bool IsValidEndpoint(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 4096 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return false;
        var host = uri.IdnHost;
        return host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("updates.push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("web.push.apple.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidKey(string? key, int byteLength)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(key);
            return bytes.Length == byteLength && (byteLength != 65 || bytes[0] == 4);
        }
        catch (FormatException) { return false; }
    }

    public static async Task<IResult> SubscribeAsync(PushSubscribeRequest req, HttpContext http, IDbContextFactory<AppDbContext> dbFactory)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        if (!IsValidEndpoint(req.Endpoint) || req.Keys is null || !IsValidKey(req.Keys.P256dh, 65) || !IsValidKey(req.Keys.Auth, 16))
            return Results.BadRequest();
        await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(p => p.Endpoint == req.Endpoint, http.RequestAborted);
        if (existing is not null && existing.UserId != userId) return Results.Conflict();
        if (existing is null)
        {
            if (await db.PushSubscriptions.CountAsync(p => p.UserId == userId, http.RequestAborted) >= 20)
                return Results.BadRequest("Limite de 20 appareils atteinte.");
            existing = new PushSubscription { UserId = userId, Endpoint = req.Endpoint };
            db.PushSubscriptions.Add(existing);
        }
        existing.P256dh = req.Keys.P256dh;
        existing.Auth = req.Keys.Auth;
        var userAgent = http.Request.Headers.UserAgent.ToString();
        existing.UserAgent = userAgent[..Math.Min(userAgent.Length, 512)];
        try { await db.SaveChangesAsync(http.RequestAborted); }
        catch (DbUpdateException) { return Results.Conflict(); }
        return Results.Ok();
    }

    public static async Task<IResult> UnsubscribeAsync(PushUnsubscribeRequest req, HttpContext http, IDbContextFactory<AppDbContext> dbFactory)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Endpoint.Length > 4096) return Results.BadRequest();
        await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        await db.PushSubscriptions.Where(p => p.Endpoint == req.Endpoint && p.UserId == userId).ExecuteDeleteAsync(http.RequestAborted);
        return Results.Ok();
    }
}
