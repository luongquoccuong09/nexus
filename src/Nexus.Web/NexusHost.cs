namespace Nexus.Web;

public static class NexusHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.UseStaticFiles();
        app.MapGet("/api/version", () => new
        {
            version = typeof(NexusHost).Assembly.GetName().Version!.ToString(3)
        });
        app.MapFallback("/api/{**path}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");
        return app;
    }
}
