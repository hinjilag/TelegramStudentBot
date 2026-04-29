namespace TelegramStudentBot.MiniApp;

public static class MiniAppEndpointExtensions
{
    public static IEndpointRouteBuilder MapMiniAppEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/miniapp");
        var groupMiniApp = endpoints.MapGroup("/api/group-miniapp");

        group.MapGet("/state", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(miniAppService.GetState(identity!));
        });

        group.MapGet("/groups", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            string directionCode) =>
        {
            if (!auth.TryAuthenticate(httpContext, out _, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            if (string.IsNullOrWhiteSpace(directionCode))
                return Results.Json(new { error = "directionCode is required." }, statusCode: StatusCodes.Status400BadRequest);

            return Results.Ok(miniAppService.GetGroups(directionCode));
        });

        group.MapPut("/schedule", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppScheduleSelectionRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.SaveScheduleSelectionAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapDelete("/schedule", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            await miniAppService.ClearScheduleSelectionAsync(identity!, cancellationToken);
            return Results.Ok(miniAppService.GetState(identity!));
        });

        group.MapPost("/homework", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppHomeworkCreateRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.CreateHomeworkAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/plan", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppPersonalTaskCreateRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.CreatePersonalTaskAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPatch("/tasks/{taskId}/completion", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            string taskId,
            MiniAppTaskCompletionRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.SetTaskCompletionAsync(identity!, taskId, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapDelete("/tasks/{taskId}", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            string taskId,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.DeleteTaskAsync(identity!, taskId, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/favorite-subjects/toggle", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppFavoriteSubjectRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.ToggleFavoriteSubjectAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPut("/reminders", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppReminderUpdateRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.UpdateReminderAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/timers/start", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            MiniAppTimerStartRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.StartTimerAsync(identity!, request, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/timers/stop", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            MiniAppService miniAppService,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await miniAppService.StopTimerAsync(identity!, cancellationToken);
                return Results.Ok(miniAppService.GetState(identity!));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        groupMiniApp.MapGet("/state", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out var chatId, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(miniAppService.GetState(identity!, chatId));
        });

        groupMiniApp.MapGet("/groups", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService,
            string directionCode) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out _, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(directionCode))
                return Results.Json(new { error = "directionCode is required." }, statusCode: StatusCodes.Status400BadRequest);

            return Results.Ok(miniAppService.GetGroups(directionCode));
        });

        groupMiniApp.MapPut("/schedule", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService,
            MiniAppScheduleSelectionRequest request) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out var chatId, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                miniAppService.SaveScheduleSelection(chatId, request);
                return Results.Ok(miniAppService.GetState(identity!, chatId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        groupMiniApp.MapDelete("/schedule", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out var chatId, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            miniAppService.ClearScheduleSelection(chatId);
            return Results.Ok(miniAppService.GetState(identity!, chatId));
        });

        groupMiniApp.MapPost("/homework", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService,
            MiniAppHomeworkCreateRequest request) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out var chatId, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                miniAppService.CreateHomework(identity!, chatId, request);
                return Results.Ok(miniAppService.GetState(identity!, chatId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        groupMiniApp.MapPut("/reminders", async (
            HttpContext httpContext,
            MiniAppAuthService auth,
            GroupMiniAppAccessService access,
            GroupMiniAppService miniAppService,
            GroupMiniAppReminderUpdateRequest request) =>
        {
            if (!auth.TryAuthenticate(httpContext, out var identity, out var authError))
                return Results.Json(new { error = authError }, statusCode: StatusCodes.Status401Unauthorized);

            if (!access.TryResolveChatAccess(httpContext, identity!, out var chatId, out var accessError))
                return Results.Json(new { error = accessError }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                miniAppService.UpdateReminder(chatId, request);
                return Results.Ok(miniAppService.GetState(identity!, chatId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        return endpoints;
    }
}
