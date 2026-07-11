using System.Net;
using System.Net.Http.Json;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

public sealed class RateLimitingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RateLimitingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_EleventhAttemptWithinWindow_IsRateLimited()
    {
        using var client = _factory.CreateClient();
        var payload = new
        {
            email = "inexistente@example.com",
            password = "Invalid123!",
            rememberMe = false,
        };

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", payload);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
