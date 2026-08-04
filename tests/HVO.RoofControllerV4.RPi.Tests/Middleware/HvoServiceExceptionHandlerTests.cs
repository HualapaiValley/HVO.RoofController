using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Tests.Middleware;

[TestClass]
public sealed class HvoServiceExceptionHandlerTests
{
    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnBadRequest_ForArgumentException()
    {
        using var services = CreateServices();
        var context = CreateHttpContext(services);
        var handler = CreateHandler(services);
        var exception = new ArgumentException("bad request");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        await AssertProblemDetailsAsync(context, "Bad Request", exception.Message);
    }

    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnTimeout_ForTimeoutException()
    {
        using var services = CreateServices();
        var context = CreateHttpContext(services);
        var handler = CreateHandler(services);
        var exception = new TimeoutException("timed out");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.RequestTimeout);
        await AssertProblemDetailsAsync(context, "Request Timeout", exception.Message);
    }

    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnInternalServerError_ForUnknownException()
    {
        using var services = CreateServices();
        var context = CreateHttpContext(services);
        var handler = CreateHandler(services);
        var exception = new InvalidOperationException("boom");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        var problem = await AssertProblemDetailsAsync(context, "Internal Server Error", "An unexpected error occurred. Use the trace ID when contacting support.");
        problem.Detail.Should().NotContain(exception.Message);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["timestamp"] = DateTime.UtcNow;
            };
        });
        return services.BuildServiceProvider();
    }

    private static HvoServiceExceptionHandler CreateHandler(IServiceProvider services) =>
        new(
            NullLogger<HvoServiceExceptionHandler>.Instance,
            services.GetRequiredService<IProblemDetailsService>());

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/test";
        return context;
    }

    private static async Task<ProblemDetails> AssertProblemDetailsAsync(HttpContext context, string expectedTitle, string expectedDetail)
    {
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Body.Seek(0, SeekOrigin.Begin);

        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        problem.Should().NotBeNull();
        problem!.Title.Should().Be(expectedTitle);
        problem.Detail.Should().Be(expectedDetail);
        problem.Instance.Should().Be("GET /api/test");
        problem.Extensions.Should().ContainKey("traceId");
        problem.Extensions.Should().ContainKey("timestamp");
        return problem;
    }
}
