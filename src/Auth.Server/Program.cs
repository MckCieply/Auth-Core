using Auth.Infrastructure;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Admin;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Keys;
using Auth.Server.Lockout;
using Auth.Server.Login;
using Auth.Server.Network;
using Auth.Server.RateLimiting;
using Auth.Server.Seeding;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Auth.Server.Tokens;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// The operator's commands run the same binary without the host: no listener, no background service, no key material.
if (args is ["admin", .. var adminArguments])
{
    return await AdminCli.RunAsync(adminArguments, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
// No Server header (spec 0008). Only a live stack shows it: the test server never sends one.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Fail fast on missing or broken key material, before anything else is built.
var keys = KeyMaterialLoader.LoadAll(builder.Configuration);
builder.Services.AddSingleton(keys);
// Fail fast on missing or invalid mail settings too.
builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
// Fail fast on a bad proxy list, a bad rate limit or a bad audit retention, naming the key (spec 0008).
var proxies = ProxySettings.Load(builder.Configuration);
builder.Services.AddSingleton(proxies);
builder.Services.AddSingleton(RateLimitSettings.Load(builder.Configuration));
builder.Services.AddSingleton(AuditSettings.Load(builder.Configuration));
builder.Services.AddSingleton<SlidingWindowLimiter>();
// JSON property names are snake_case (spec 0005 → General rules). The bodies written before are unaffected: their
// property names already are.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
// Nothing of Auth-Core uses Data Protection, and the production container's file system is read-only: keys stay in memory.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAuthOpenApi();
builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddAuthPersistence(builder.Configuration);
// OpenIddict takes its clock from DI; tests replace this registration to move time.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddAuthOpenIddict(builder.Configuration, keys, builder.Environment.IsDevelopment());
builder.Services.AddSingleton<TokenPruner>();
builder.Services.AddSingleton<LockoutPruner>();
builder.Services.AddSingleton<AuditPruner>();
builder.Services.AddSingleton<LoginStreakStore>();
builder.Services.AddSingleton<MailRequestStore>();
builder.Services.AddSingleton<MailDispatchSignal>();
builder.Services.AddSingleton<MailDispatcher>();
builder.Services.AddSingleton<EmailPruner>();
builder.Services.AddSingleton<MailComposer>();
builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
builder.Services.AddSingleton<DecoyPasswordHash>();
builder.Services.AddHostedService<TokenPruningService>();
builder.Services.AddHostedService<LockoutPruningService>();
builder.Services.AddHostedService<AuditPruningService>();
builder.Services.AddHostedService<MailDispatchService>();
builder.Services.AddHostedService<EmailPruningService>();

var app = builder.Build();

// Made now, not by the first login with an unknown email, which would then take twice as long as the next one.
_ = app.Services.GetRequiredService<DecoyPasswordHash>();

if (app.Configuration.GetValue<bool>("Auth:Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
}

// Which manifest is active: the product's file, or the last valid one. Never a reason to stop.
await app.Services.GetRequiredService<ManifestActivator>().ActivateAsync(app.Lifetime.ApplicationStopping);

await DevUserSeeder.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);

// The outermost handler and the headers come first: whatever the pipeline answers, a 404, a 429 or an exception included, has
// them. Routing comes right after them: called here, it is not put in front of them by WebApplication, so that an exception of
// the route matcher is answered by the handler too. Then the client address (the rate limiter and the audit log are about it)
// and the limiter, then the JSON charset guard: all before authentication, because login is answered inside it.
app.UseErrorHandling();
app.UseSecurityHeaders();
app.UseRouting();
app.UseClientAddress(proxies);
app.UseMiddleware<RateLimitMiddleware>();
app.UseJsonCharsetGuard();
app.UseAuthentication();
app.UseAuthorization();

app.MapAccountApi();
app.MapTenancyApi();
app.MapAuthOpenApi();

app.Run();
return 0;

public partial class Program;
