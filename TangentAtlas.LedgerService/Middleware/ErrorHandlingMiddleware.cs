// <copyright file="ErrorHandlingMiddleware.cs" company="Tangent Atlas">
//    Copyright © AtlasLedger. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
using System.Net;
using System.Text.Json;

namespace TangentAtlas.LedgerService.Middleware;

/// <summary>
/// Global exception handler for the API. Catches unhandled exceptions and returns a consistent
/// JSON error envelope.
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    /// <summary>
    /// The options used to serialize the error envelope. Cached and reused per CA1869.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate next;
    private readonly ILogger<ErrorHandlingMiddleware> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorHandlingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the request pipeline.</param>
    /// <param name="logger">The log device.</param>
    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    /// <summary>
    /// Processes the request and catches any unhandled exceptions.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A <see cref="Task"/> that represents the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await this.next(context);
        }
        catch (Exception exception)
        {
            this.logger.LogError(
                exception,
                "[AtlasLedger] Unhandled exception — {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            await HandleExceptionAsync(context, exception);
        }
    }

    /// <summary>
    /// Writes a consistent JSON error envelope for the given exception.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="exception">The exception to translate into a response.</param>
    /// <returns>A <see cref="Task"/> that represents the asynchronous operation.</returns>
    private static Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        (HttpStatusCode statusCode, string code) = exception switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UNAUTHORIZED"),
            ArgumentNullException => (HttpStatusCode.BadRequest, "BAD_REQUEST"),
            ArgumentException => (HttpStatusCode.BadRequest, "BAD_REQUEST"),
            KeyNotFoundException => (HttpStatusCode.NotFound, "NOT_FOUND"),
            NotImplementedException => (HttpStatusCode.NotImplemented, "NOT_IMPLEMENTED"),
            _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR"),
        };

        var body = new
        {
            error = new
            {
                source = "AtlasLedgerApi",
                code,
                message = statusCode == HttpStatusCode.InternalServerError
                    ? "An unexpected error occurred."
                    : exception.Message,
                traceId = context.TraceIdentifier,
            },
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
