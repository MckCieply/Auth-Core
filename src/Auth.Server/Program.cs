using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Email;
using Auth.Server.Keys;
using Auth.Server.Lockout;
using Auth.Server.Login;
using Auth.Server.Seeding;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Auth.Server.Tokens;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Fail fast on missing or broken key material, before anything else is built.
var keys = KeyMaterialLoader.LoadAll(builder.Configuration);
builder.Services.AddSingleton(keys);
// Fail fast on missing or invalid mail settings too.
builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddAuthPersistence(builder.Configuration);
// OpenIddict takes its clock from DI; tests replace this registration to move time.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddAuthOpenIddict(builder.Configuration, keys, builder.Environment.IsDevelopment());
builder.Services.AddSingleton<TokenPruner>();
builder.Services.AddSingleton<LockoutPruner>();
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

app.MapHealthChecks("/auth/health");
app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync);
app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync);
app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync);
app.MapPost(MailRequestEndpoint.ForgotPasswordPath,
    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
        MailRequestEndpoint.HandleAsync(MailKind.PasswordReset, http, normalizer, requests, signal));
app.MapPost(MailRequestEndpoint.VerifyEmailRequestPath,
    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
        MailRequestEndpoint.HandleAsync(MailKind.EmailVerification, http, normalizer, requests, signal));
app.MapPost(ResetPasswordEndpoint.Path, ResetPasswordEndpoint.HandleAsync);
app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync);

app.Run();

public partial class Program;
