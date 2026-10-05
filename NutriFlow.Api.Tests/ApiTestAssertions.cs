using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

internal static class ApiTestAssertions
{
    public static async Task AssertStatusAsync(
        WebApplicationFactory<Program> factory,
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        if (response.StatusCode == expectedStatus)
        {
            return;
        }

        StringBuilder description = new();
        description.AppendLine($"Expected HTTP {(int)expectedStatus} ({expectedStatus}); received {(int)response.StatusCode} ({response.StatusCode}).");
        description.AppendLine($"Request: {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}");
        description.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
        try
        {
            string body = await response.Content.ReadAsStringAsync();
            description.AppendLine($"Response body: {body[..Math.Min(body.Length, 4096)]}");
        }
        catch (Exception exception)
        {
            description.AppendLine($"Response body unavailable: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            description.AppendLine(factory.Services.GetRequiredService<TestErrorLogProvider>().DescribeErrors());
        }
        catch (Exception exception)
        {
            description.AppendLine($"Server logs unavailable: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            NutriFlowDbContext db = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            description.AppendLine($"Database: {db.Database.GetDbConnection().DataSource}");
            description.AppendLine($"Content root: {scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath}");
        }
        catch (Exception exception)
        {
            description.AppendLine($"Storage diagnostics unavailable: {exception.GetType().Name}: {exception.Message}");
        }

        Assert.True(response.StatusCode == expectedStatus, description.ToString());
    }
}
