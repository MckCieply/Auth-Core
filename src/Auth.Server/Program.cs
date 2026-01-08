using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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
