using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Common.Extensions;
using PartyApp.Api.Common.Middleware;
using PartyApp.Api.Modules.Auth.Services;

namespace PartyApp.UnitTests.Common;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int StatusCode, string Body)> InvokeAsync(RequestDelegate next)
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        ExceptionHandlingMiddleware middleware = new(next, NullLogger<ExceptionHandlingMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task InvokeAsync_AppException_ReturnsBadRequestWithMessage()
    {
        (int statusCode, string body) = await InvokeAsync(
            _ => throw new AppException("Неверный промокод"));

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("Неверный промокод");
    }

    [Fact]
    public async Task InvokeAsync_DerivedAppException_ReturnsBadRequest()
    {
        (int statusCode, string body) = await InvokeAsync(
            _ => throw new AuthException("Пароль слишком короткий"));

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Пароль слишком короткий");
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_ReturnsInternalServerErrorWithoutDetails()
    {
        (int statusCode, string body) = await InvokeAsync(
            _ => throw new InvalidOperationException("секретные детали"));

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Произошла внутренняя ошибка");
        body.Should().NotContain("секретные детали");
    }

    [Fact]
    public async Task InvokeAsync_WithoutException_DoesNotTouchResponse()
    {
        (int statusCode, string body) = await InvokeAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            await context.Response.WriteAsync("нетронуто");
        });

        statusCode.Should().Be(StatusCodes.Status204NoContent);
        body.Should().Be("нетронуто");
    }
}