using WorkSpaceServer.Hubs;
using WorkSpaceServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(opts =>
{
    opts.MaximumReceiveMessageSize = 20 * 1024 * 1024; // 20 MB — enough for large .md files
});

builder.Services.AddSingleton<SessionService>();

builder.Services.AddCors(opts =>
    opts.AddPolicy("AllowAll", p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()));

var app = builder.Build();

app.UseCors("AllowAll");

app.MapHub<SyncHub>("/sync");

app.MapGet("/", () => Results.Ok(new
{
    name    = "WorkSpace Sync Server",
    version = "1.0",
    status  = "running",
    time    = DateTime.UtcNow
}));

Console.WriteLine("WorkSpace Sync Server starting on http://0.0.0.0:5000");
Console.WriteLine("Share this machine's IP with users so they can connect.");

app.Run("http://0.0.0.0:5000");
