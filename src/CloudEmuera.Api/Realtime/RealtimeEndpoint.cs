using CloudEmuera.Api.Security;
using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Contracts;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Contracts.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Net.WebSockets;

namespace CloudEmuera.Api.Realtime;

/// <summary>HTTP upgrade boundary for the native realtime v3 protocol.</summary>
public sealed class RealtimeEndpoint(
    IServiceScopeFactory scopeFactory,
    RealtimeAuthorizationGate authorization,
    RealtimeGatewayOptions options,
    RealtimeConnectionRegistry registry,
    IRealtimeSessionRegistry sessionRegistry,
    ISessionCommandGate commandGate,
    RealtimeEnvelopeCodec codec,
    WorkerManager workerManager,
    SessionCommandReadiness readiness)
{
    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "NOT_A_WEBSOCKET", "WebSocket upgrade is required.").ConfigureAwait(false);
            return;
        }

        if (!HasRequestedSubprotocol(context))
        {
            context.Response.Headers[HeaderNames.SecWebSocketProtocol] = RealtimeProtocol.Subprotocol;
            await RejectAsync(context, StatusCodes.Status426UpgradeRequired, "REALTIME_PROTOCOL_REQUIRED", "The realtime v3 subprotocol is required.").ConfigureAwait(false);
            return;
        }

        RealtimeConnectionIdentity? identity = authorization.ReadIdentity(context.User);
        if (identity is null)
        {
            await RejectAsync(context, StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "A live authentication session is required.").ConfigureAwait(false);
            return;
        }

        // Keep the existing P1-08 upgrade validator as the first scoped gate;
        // the connection-specific gate below also checks current user status
        // and password-change state without retaining its scoped services.
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            RealtimeUpgradeValidator validator = scope.ServiceProvider.GetRequiredService<RealtimeUpgradeValidator>();
            if (!await validator.IsUpgradeAllowedAsync(context, context.RequestAborted).ConfigureAwait(false))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "REALTIME_SESSION_REJECTED", "The realtime session is not allowed.").ConfigureAwait(false);
                return;
            }
        }

        RealtimeAuthorizationResult auth = await authorization.AuthenticateConnectionAsync(identity, context.RequestAborted).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            await RejectAsync(
                context,
                auth.Status == RealtimeAuthorizationStatus.AuthenticationExpired
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status403Forbidden,
                auth.Status == RealtimeAuthorizationStatus.PasswordChangeRequired ? "PASSWORD_CHANGE_REQUIRED" : "AUTHENTICATION_EXPIRED",
                "The realtime authentication is not available.").ConfigureAwait(false);
            return;
        }

        if (!readiness.IsReady)
        {
            await RejectAsync(context, StatusCodes.Status503ServiceUnavailable, "SERVICE_NOT_READY", readiness.Reason).ConfigureAwait(false);
            return;
        }
        if (workerManager.IsDraining)
        {
            await RejectAsync(context, StatusCodes.Status503ServiceUnavailable, "API_DRAINING", "The API is draining.").ConfigureAwait(false);
            return;
        }

        RealtimeConnectionAdmission? admission = registry.TryReserve(identity.UserId);
        if (admission is null)
        {
            await RejectAsync(context, StatusCodes.Status503ServiceUnavailable, "REALTIME_CAPACITY_EXCEEDED", "Realtime connection capacity is exhausted.").ConfigureAwait(false);
            return;
        }

        WebSocket? socket = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync(RealtimeProtocol.Subprotocol).ConfigureAwait(false);
            var connection = new RealtimeConnection(
                socket,
                admission,
                registry,
                sessionRegistry,
                commandGate,
                authorization,
                identity,
                options,
                codec,
                () => workerManager.IsDraining,
                context.RequestServices.GetRequiredService<ILogger<RealtimeConnection>>());
            await connection.RunAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            admission.Dispose();
            if (socket is not null && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "accept_failed", CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private static bool HasRequestedSubprotocol(HttpContext context)
    {
        string value = context.Request.Headers[HeaderNames.SecWebSocketProtocol].ToString();
        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(RealtimeProtocol.Subprotocol, StringComparer.Ordinal);
    }

    private static async Task RejectAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
            return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ApiError(code, message, $"req_{Guid.CreateVersion7():N}"), context.RequestAborted).ConfigureAwait(false);
    }
}
