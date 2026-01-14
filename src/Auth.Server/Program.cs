using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Auth.Server.Keys;
using Auth.Server.Login;
using Auth.Server.Seeding;
using Auth.Server.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Fail fast on missing or broken key material, before anything else is built.
var keys = KeyMaterialLoader.LoadAll(builder.Configuration);
builder.Services.AddSingleton(keys);
builder.Services.AddHealthChecks();
builder.Services.AddAuthPersistence(builder.Configuration);
// OpenIddict takes its clock from DI; tests replace this registration to move time.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddAuthOpenIddict(builder.Configuration, keys, builder.Environment.IsDevelopment());

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Auth:Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
}

await DevUserSeeder.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);

app.MapHealthChecks("/auth/health");
app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync);

app.Run();

public partial class Program;
