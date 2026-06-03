using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Builder;
using Shared.Exceptions;
using Shared.Models;
using System.Net;
using System.Text.Json;

namespace Shared.Middlewares
{
    public class GlobalExceptionMiddleware : IExceptionHandler
    {
        private readonly ILogger<GlobalExceptionMiddleware> _logger;

        public GlobalExceptionMiddleware(ILogger<GlobalExceptionMiddleware> logger)
        {
            _logger = logger;
        }

        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "An error occurred: {Message}", exception.Message);

            object response;
            int statusCode;

            if (exception is BaseDomainException domainEx)
            {
                statusCode = (int)domainEx.StatusCode;
                var errors = new List<ApiError>
                {
                    new() { Code = domainEx.ErrorCode, Message = domainEx.Message }
                };

                response = statusCode switch
                {
                    400 => ResponseBuilder.BadRequest("Yeu cau khong hop le", errors),
                    401 => ResponseBuilder.Unauthorized(domainEx.Message),
                    403 => ResponseBuilder.Forbidden(domainEx.Message),
                    404 => ResponseBuilder.NotFound(domainEx.Message),
                    409 => ResponseBuilder.Conflict(domainEx.Message, errors),
                    429 => ResponseBuilder.Error("Qua nhieu yeu cau. Vui long thu lai sau.", errors),
                    _ => ResponseBuilder.Error(domainEx.Message, errors)
                };
            }
            else if (exception is UnauthorizedAccessException)
            {
                statusCode = (int)HttpStatusCode.Unauthorized;
                response = ResponseBuilder.Unauthorized(exception.Message);
            }
            else if (exception is KeyNotFoundException)
            {
                statusCode = (int)HttpStatusCode.NotFound;
                response = ResponseBuilder.NotFound(exception.Message);
            }
            else if (exception is ArgumentException or FormatException)
            {
                statusCode = (int)HttpStatusCode.BadRequest;
                response = ResponseBuilder.BadRequest(exception.Message);
            }
            else if (exception is InvalidOperationException)
            {
                statusCode = (int)HttpStatusCode.Conflict;
                response = ResponseBuilder.Conflict(exception.Message);
            }
            else if (exception is DbUpdateException)
            {
                statusCode = (int)HttpStatusCode.Conflict;
                response = ResponseBuilder.Conflict("Database update failed. Please check whether the referenced data exists or already conflicts.");
            }
            else
            {
                statusCode = (int)HttpStatusCode.InternalServerError;
                response = ResponseBuilder.InternalServerError(exception.Message);
            }

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json";

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await httpContext.Response.WriteAsync(json, cancellationToken);

            return true;
        }
    }
}
