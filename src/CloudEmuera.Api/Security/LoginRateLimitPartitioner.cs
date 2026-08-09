using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Identity;

namespace CloudEmuera.Api.Security;

/// <summary>Builds a bounded in-process limiter key without trusting forwarded headers.</summary>
public static class LoginRateLimitPartitioner
{
    private const string PartitionItem = "cloudemuera.login-rate-partition";

    public static async Task CaptureAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.Equals("/api/v1/auth/login", StringComparison.Ordinal)) return;
        context.Request.EnableBuffering();
        string email = string.Empty;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("email", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                email = value.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        finally { context.Request.Body.Position = 0; }
        context.Items[PartitionItem] = CreateKey(context, email);
    }

    public static string GetKey(HttpContext context) => context.Items.TryGetValue(PartitionItem, out object? value) && value is string key
        ? key : CreateKey(context, string.Empty);

    private static string CreateKey(HttpContext context, string email)
    {
        string normalized;
        try { normalized = IdentityValidation.NormalizeEmail(email); }
        catch (IdentityValidationException) { normalized = "invalid"; }
        string source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}\n{normalized}")));
    }
}
