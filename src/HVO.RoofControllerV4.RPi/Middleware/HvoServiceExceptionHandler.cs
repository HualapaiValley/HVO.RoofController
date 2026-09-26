using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace HVO.RoofControllerV4.RPi.Middleware
{
    /// <summary>
    /// Global exception handler middleware for HVO services that converts exceptions to appropriate HTTP responses.
    /// Follows the HVOv9 standards for exception handling and ProblemDetails responses.
    /// </summary>
    public class HvoServiceExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<HvoServiceExceptionHandler> _logger;
        private readonly IProblemDetailsService _problemDetailsService;

        public HvoServiceExceptionHandler(
            ILogger<HvoServiceExceptionHandler> logger,
            IProblemDetailsService problemDetailsService)
        {
            _logger = logger;
            _problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext, 
            Exception exception, 
            CancellationToken cancellationToken)
        {
            var (statusCode, title) = MapExceptionToResponse(exception);
            
            _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

            var problemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = statusCode >= StatusCodes.Status500InternalServerError
                    ? "An unexpected error occurred. Use the trace ID when contacting support."
                    : exception.Message
            };

            httpContext.Response.StatusCode = statusCode;
            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = problemDetails
            });
        }

        private static (int StatusCode, string Title) MapExceptionToResponse(Exception exception)
        {
            return exception switch
            {
                ArgumentNullException => ((int)HttpStatusCode.BadRequest, "Bad Request"),
                ArgumentException => ((int)HttpStatusCode.BadRequest, "Bad Request"),
                UnauthorizedAccessException => ((int)HttpStatusCode.Unauthorized, "Unauthorized"),
                NotImplementedException => ((int)HttpStatusCode.NotImplemented, "Not Implemented"),
                TimeoutException => ((int)HttpStatusCode.RequestTimeout, "Request Timeout"),
                
                // All other exceptions, including InvalidOperationException, should be 500
                // Controllers should handle Result<T> failures explicitly for proper status codes
                _ => ((int)HttpStatusCode.InternalServerError, "Internal Server Error")
            };
        }
    }
}
