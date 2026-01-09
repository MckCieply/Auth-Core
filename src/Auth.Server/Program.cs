using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Auth.Server.Keys;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Fail fast on missing or broken key material, before anything else is built.
builder.Services.AddSingleton(KeyMaterialLoader.LoadAll(builder.Configuration));
builder.Services.AddHealthChecks();
builder.Services.AddAuthPersistence(builder.Configuration);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Auth:Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/auth/health");

app.Run();

public partial class Program;
