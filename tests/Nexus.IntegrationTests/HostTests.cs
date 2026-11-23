using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;

namespace Nexus.IntegrationTests;

public class HostTests
{
    [Fact]
    public async Task HostServesVersionAndReturns404ForUnknownApi()
    {
        await using var app = NexusHost.Build(["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var payload = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/version");
        Assert.True(Version.TryParse(payload?["version"], out _));
        using var missing = await client.GetAsync("/api/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
