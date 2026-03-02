using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Tests.Middleware;

[TestClass]
public sealed class HvoServiceExceptionHandlerTests
{
    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnBadRequest_ForArgumentException()
    {
        var context = CreateHttpContext();
        var handler = new HvoServiceExceptionHandler(NullLogger<HvoServiceExceptionHandler>.Instance);
        var exception = new ArgumentException("bad request");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        await AssertProblemDetailsAsync(context, "Bad Request", exception.Message);
    }

    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnTimeout_ForTimeoutException()
    {
        var context = CreateHttpContext();
        var handler = new HvoServiceExceptionHandler(NullLogger<HvoServiceExceptionHandler>.Instance);
        var exception = new TimeoutException("timed out");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.RequestTimeout);
        await AssertProblemDetailsAsync(context, "Request Timeout", exception.Message);
    }

    [TestMethod]
    public async Task TryHandleAsync_ShouldReturnInternalServerError_ForUnknownException()
    {
        var context = CreateHttpContext();
        var handler = new HvoServiceExceptionHandler(NullLogger<HvoServiceExceptionHandler>.Instance);
        var exception = new InvalidOperationException("boom");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        await AssertProblemDetailsAsync(context, "Internal Server Error", exception.Message);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/test";
        return context;
    }

    private static async Task AssertProblemDetailsAsync(HttpContext context, string expectedTitle, string expectedDetail)
    {
    context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Seek(0, SeekOrigin.Begin);

        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        problem.Should().NotBeNull();
        problem!.Title.Should().Be(expectedTitle);
        problem.Detail.Should().Be(expectedDetail);
        problem.Instance.Should().Be("/api/test");
    }
}
