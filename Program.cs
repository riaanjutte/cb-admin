using System.Security.Claims;
using CbAdmin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using LogLevel = NLog.LogLevel;
using System.Globalization;

// Configure the culture
var cultureInfo = new CultureInfo("en-GB"); // For UK-style dates: dd/MM/yyyy
cultureInfo.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
cultureInfo.DateTimeFormat.LongDatePattern = "dd MMMM yyyy";

CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

// Set up logging
var config = new LoggingConfiguration();
var logFileName = "cb-admin";
var fileTarget = new FileTarget
{
    FileName = $"{logFileName}.log",
    ArchiveFileName = Layout.FromString(logFileName + "-{###}.log"),
    ArchiveNumbering = ArchiveNumberingMode.Date,
    ArchiveEvery = FileArchivePeriod.Day,
    MaxArchiveFiles = 30,
    Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}",
};

config.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget, "*");
LogManager.Configuration = config;
var logger = LogManager.GetCurrentClassLogger();
logger.Debug("Init complete");

// Set up the web application via the various builders
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

var client = new DataClient(builder.Configuration);

// This is Discord authentication, which is used to authenticate Combat Box administrators.
services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "Discord";
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.LogoutPath = "/signout";
    })
    .AddDiscord(options =>
    {
        options.ClientId = builder.Configuration["Discord:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Discord:ClientSecret"] ?? string.Empty;
        options.Scope.Add("identify");
    });

services.AddMvc();
services.AddRazorPages(opts =>
{
    opts.Conventions.AuthorizePage("/admin");
});


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

var dataClient = new DataClient(builder.Configuration);

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        bool isAdmin = dataClient.GetAdminInfo(context.User) != null;

        if (isAdmin)
        {
            // The user is authorized, so call the next middleware
            await next();
        }
        else
        {
            // The user is not authorized, so display a "Forbidden" response
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("You are not an authorized CB Admin.");
        }
    }
    else
    {
        // The user is not authenticated, so challenge them
        await context.ChallengeAsync();
    }
});

// Configure the application to serve /admin as the default URL.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        context.Response.Redirect("/user_bans");
        return;
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true
});

// Admin API
app.Map("/data/get_user_info", async (HttpContext context, string name) =>
{
    var admin = dataClient.GetAdminInfo(context.User);
    if (admin == null)
    {

        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("You are not an authorized CB Admin.");
        return;
    }

    logger.Debug($"Admin {admin.Name} requesting user info for {name}");

    var output = dataClient.LoadPlayerInfo(name);
    await context.Response.WriteAsJsonAsync(output);
}).RequireAuthorization();

app.MapPost("/data/add_ban", (HttpContext context, int id) =>
{
    string? reason = context.Request.Form["reason"];
    int duration = int.Parse(context.Request.Form["duration"].ToString());
    if (string.IsNullOrEmpty(reason) || duration <= 0)
    {
        return;
    }
    var adminInfo = dataClient.GetAdminInfo(context.User);
    if (adminInfo != null)
    {
        var name = dataClient.GetLastPlayerName(id);

        logger.Debug($"Admin {adminInfo.Name} bans player {name} (#{id}) for {TimeSpan.FromMinutes(duration):g}. Reason: {reason}");

        dataClient.AddNewBan(adminInfo.Id, id, reason, duration);
        context.Response.Redirect($"/user_bans?name={name}");
    }
}).RequireAuthorization();

app.MapPost("/data/change_ban", (HttpContext context, int banId) =>
{
    int duration = int.Parse(context.Request.Form["duration"].ToString());
    if (duration < 0)
    {
        return;
    }
    var adminInfo = dataClient.GetAdminInfo(context.User);
    if (adminInfo != null)
    {
        var name = context.Request.Form["playerName"];

        logger.Debug($"Admin {adminInfo.Name} changes ban duration for player {name} to {TimeSpan.FromMinutes(duration):g}.");
        dataClient.ChangeBanDuration(banId, duration, adminInfo.Id);

        context.Response.Redirect($"/user_bans?name={name}");
    }
}).RequireAuthorization();

app.Run();
return;

