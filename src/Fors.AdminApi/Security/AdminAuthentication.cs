using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Fors.AdminApi.Common;
using Fors.Infrastructure.Persistence;
using Fors.Infrastructure.Security;

namespace Fors.AdminApi.Security;

public static partial class AdminAuthentication
{
    /// <summary>Where every sign-in starts; the effective sign-in values are read before it (<see cref="AdminSignIn.Refreshes"/>).</summary>
    public const string LoginPath = "/bff/login";

    public const string LogoutPath = "/bff/logout";

    public const string UserPath = "/bff/user";

    /// <summary>The <c>code</c> of the problem a sign-in answers while the admin API has no usable identity provider.</summary>
    public const string SignInNotConfiguredCode = "sign_in_not_configured";

    /// <summary>
    /// Normalises identity-provider claims to the names the API authorises on ("roles", "departmentCodes", "teamCodes"):
    /// expands JSON-array claims, copies differently named claims, and derives roles from group membership. The group
    /// claims themselves are then dropped: they are only needed for the roles and team codes derived from them, a user can be
    /// in hundreds of groups (Active Directory through Keycloak, Entra ID), and every claim kept goes into the session cookie.
    /// With <paramref name="departments"/> (the active departments that have member groups), a user in one of a
    /// department's groups also gets its code (<see cref="DepartmentMembership"/>); those codes are returned.
    /// </summary>
    public static IReadOnlyList<string> ApplyClaimMapping(ClaimsPrincipal? principal, OidcOptions options, IEnumerable<DepartmentGroups>? departments = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return [];
        }
        var sources = new[] { options.RoleClaim, options.DepartmentClaim, options.TeamClaim, options.GroupClaim };
        foreach (var claim in identity.Claims.Where(c => sources.Contains(c.Type) && c.Value.StartsWith('[')).ToArray())
        {
            using var json = JsonDocument.Parse(claim.Value);
            identity.RemoveClaim(claim);
            foreach (var value in json.RootElement.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    identity.AddClaim(new Claim(claim.Type, value.GetString()!));
                }
            }
        }
        CopyClaims(identity, options.RoleClaim, AdminClaims.Roles);
        CopyClaims(identity, options.DepartmentClaim, AdminClaims.DepartmentCodes);
        CopyClaims(identity, options.TeamClaim, AdminClaims.TeamCodes);
        var groups = identity.FindAll(options.GroupClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, mapped) in options.RoleGroups)
        {
            if (!identity.HasClaim(AdminClaims.Roles, role) &&
                mapped.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(groups.Contains))
            {
                identity.AddClaim(new Claim(AdminClaims.Roles, role));
            }
        }

        IReadOnlyList<string> groupCodes = departments is null
            ? []
            : DepartmentMembership.CodesFromGroups(groups, [.. identity.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value)], departments);
        foreach (var code in groupCodes)
        {
            identity.AddClaim(new Claim(AdminClaims.DepartmentCodes, code));
        }

        if (options.GroupClaim is not (AdminClaims.Roles or AdminClaims.DepartmentCodes or AdminClaims.TeamCodes))
        {
            foreach (var claim in identity.FindAll(options.GroupClaim).ToArray())
            {
                identity.RemoveClaim(claim);
            }
        }

        return groupCodes;
    }

    private static void CopyClaims(ClaimsIdentity identity, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }
        foreach (var claim in identity.FindAll(from).ToArray())
        {
            // The group claim may serve as the team claim too; a group is then copied once however many settings name it.
            if (!identity.HasClaim(to, claim.Value))
            {
                identity.AddClaim(new Claim(to, claim.Value));
            }
        }
    }

    /// <summary>
    /// Cookie session (BFF) with OpenID Connect sign-in, antiforgery for the SPA and the authorization policies. The OIDC
    /// authority, client, secret and extra scopes are the effective sign-in values (<see cref="AdminSignIn"/>), applied each
    /// time the options are built.
    /// </summary>
    public static void AddAdminAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-XSRF-TOKEN";
            o.Cookie.Name = "__Host-fors-csrf";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
        });
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddCookie(o =>
        {
            o.Cookie.Name = "__Host-fors-admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = AdminSessionLimit.Lifetime;
            o.SlidingExpiration = false;
            o.Events.OnSigningIn = ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                AdminSessionLimit.OnSigningIn(ctx, TimeSpan.FromHours(services.GetRequiredService<IOptions<AdminOptions>>().Value.MaxSessionHours),
                    services.GetRequiredService<TimeProvider>().GetUtcNow());
                // A new sign-in (not an extension, which signs in again with the same principal) gets its session id.
                if (ctx.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == AdminClaims.Session))
                {
                    identity.AddClaim(new Claim(AdminClaims.Session, AdminActor.NewSessionId()));
                    var actor = AdminActor.Of(ctx.HttpContext, ctx.Principal);
                    SecurityEvents.AdminSignIn(SecurityEvents.CreateLogger(services.GetRequiredService<ILoggerFactory>()), actor.Subject,
                        actor.SourceAddress, actor.Roles, actor.Departments, actor.SessionId);
                }

                return Task.CompletedTask;
            };
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        }).AddOpenIdConnect(o =>
        {
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = AdminClaims.Roles;
            o.Scope.Add("email");
            o.Events.OnTokenValidated = async ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                // Sign-ins are rare, so the member groups are read each time: a change applies at the next sign-in.
                var departments = await services.GetRequiredService<GatewayDbContext>().Departments.AsNoTracking()
                    .Where(d => d.IsActive && d.MemberGroups.Count > 0)
                    .Select(d => new DepartmentGroups(d.CostCenterCode, d.MemberGroups))
                    .ToListAsync(ctx.HttpContext.RequestAborted);
                // The values this handler's options were built from, with the deployment's role groups.
                var values = services.GetRequiredService<AdminSignIn>().For(ctx.Options)
                    .ToOidcOptions(services.GetRequiredService<IOptions<OidcOptions>>().Value.RoleGroups);
                var groupCodes = ApplyClaimMapping(ctx.Principal, values, departments);
                // Kept with the session (and by an extension, which signs in with the same properties) for /bff/user.
                DepartmentMembership.Store(ctx.Properties ??= new AuthenticationProperties(), groupCodes);
            };
            // An expired session on an API call must answer 401 so the UI can send the user to sign in; a redirect to the
            // identity provider would be followed by fetch and fail on CORS. Only /bff/login starts a real sign-in: any other
            // challenge is sent there, because the current sign-in values are read before /bff/login's handler is created.
            o.Events.OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }

                if (!ctx.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.Redirect(QueryHelpers.AddQueryString(LoginPath, "returnUrl", ctx.Properties.RedirectUri ?? "/"));
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }

                var services = ctx.HttpContext.RequestServices;
                if (services.GetRequiredService<AdminSignIn>().For(ctx.Options).NotConfiguredReason is { } reason)
                {
                    // No identity provider to send the person to; the admin UI reads signIn.configured from /bff/user.
                    LogSignInNotConfigured(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminAuthentication)), reason);
                    throw new ApiFaultException(StatusCodes.Status409Conflict, "Sign-in is not set up yet.",
                        new Dictionary<string, object?> { ["code"] = SignInNotConfiguredCode });
                }

                return Task.CompletedTask;
            };
            // A sign-in that cannot be completed gives no session and no 500: one started before the sign-in values changed (the
            // new provider or client refuses its code), an expired, forged or replayed callback, or the provider's error. The
            // person starts again from the sign-in page.
            o.Events.OnRemoteFailure = ctx =>
            {
                var loggers = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                LogSignInFailed(loggers.CreateLogger(typeof(AdminAuthentication)), ctx.Failure?.GetType().Name ?? "unknown");
                SecurityEvents.AdminSignInFailed(SecurityEvents.CreateLogger(loggers), "callback_failed",
                    ctx.HttpContext.Items[AdminClientAddresses.ItemKey] as string);
                ctx.HandleResponse();
                ctx.Response.Redirect("/?signin=failed");
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToIdentityProviderForSignOut = async ctx =>
            {
                if (!ctx.HttpContext.RequestServices.GetRequiredService<AdminSignIn>().For(ctx.Options).IsConfigured)
                {
                    // The session is already gone (the cookie scheme signs out first); there is no provider to sign out at.
                    await ctx.Response.WriteAsJsonAsync(new { redirectUrl = "/" });
                    ctx.HandleResponse();
                    return;
                }

                ctx.ProtocolMessage.ClientId = ctx.Options.ClientId;
                ctx.ProtocolMessage.State = ctx.Options.StateDataFormat.Protect(ctx.Properties);
                if (string.IsNullOrEmpty(ctx.ProtocolMessage.IssuerAddress))
                {
                    throw new InvalidOperationException("OIDC end-session endpoint is missing.");
                }
                await ctx.Response.WriteAsJsonAsync(new { redirectUrl = ctx.ProtocolMessage.CreateLogoutRequestUrl() });
                ctx.HandleResponse();
            };
        });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(AdminPolicies.Admin, p => p.RequireRole(AdminRoles.GatewayAdmin));
            o.AddPolicy(AdminPolicies.Manage, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin));
            o.AddPolicy(AdminPolicies.Read, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.Viewer));
            o.AddPolicy(AdminPolicies.ManageKeys, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.TeamAdmin));
            o.AddPolicy(AdminPolicies.ListTeams, p => p.RequireRole(AdminRoles.GatewayAdmin, AdminRoles.DepartmentAdmin, AdminRoles.Viewer, AdminRoles.TeamAdmin));
            // The Integration API (/integration/v1): an integration's credential key with the scope, never a session.
            Integrations.IntegrationKeyHandler.AddPolicies(o);
        });
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, Integrations.IntegrationKeyHandler>(Integrations.IntegrationKeyHandler.SchemeName, _ => { });
        // Sessions live on the server: the cookie only names one, and signing out deletes it (a copied cookie stops working).
        builder.Services.AddSingleton<ITicketStore, AdminTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
        // The effective sign-in values, applied at every build of the options: the first, and each rebuild after they changed.
        builder.Services.AddSingleton<AdminSignIn>();
        builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<AdminSignIn>((o, signIn) => signIn.Configure(o));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in refused: sign-in is not configured ({Reason})")]
    private static partial void LogSignInNotConfigured(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in callback could not be completed ({FailureType}); the person is sent back to sign in")]
    private static partial void LogSignInFailed(ILogger logger, string failureType);

    public static void MapAdminAuthentication(this WebApplication app)
    {
        app.MapGet(LoginPath, (string? returnUrl) =>
        {
            var destination = returnUrl ?? "/";
            if (!destination.StartsWith('/') || destination.StartsWith("//", StringComparison.Ordinal) || destination.Contains('\\', StringComparison.Ordinal) ||
                destination.Any(char.IsControl))
            {
                return Results.Problem(statusCode: 400, detail: "The return address must be a local path.");
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = destination }, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        app.MapGet(UserPath, async (HttpContext http, IAntiforgery antiforgery, IHostEnvironment environment, IOptions<AdminOptions> admin, AdminSignIn signIn,
            TimeProvider time) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                Secure = true, HttpOnly = false, SameSite = SameSiteMode.Lax, Path = "/",
            });
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new
            {
                isAuthenticated = http.User.Identity?.IsAuthenticated == true,
                environment = environment.EnvironmentName,
                name = http.User.Identity?.Name,
                email = http.User.FindFirstValue("email"),
                roles = http.User.FindAll(AdminClaims.Roles).Select(c => c.Value).ToArray(),
                departmentCodes = http.User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).ToArray(),
                // Where each code came from: the department claim, or a member group of the department (never the group itself).
                departmentSources = DepartmentMembership.Sources(http.User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value), auth.Properties),
                teamCodes = http.User.FindAll(AdminClaims.TeamCodes).Select(c => c.Value).ToArray(),
                sessionExpiresAt = auth.Properties?.ExpiresUtc,
                sessionLimitAt = AdminSessionLimit.LimitOf(auth.Properties, TimeSpan.FromHours(admin.Value.MaxSessionHours)),
                // Read at most UserRefreshInterval before this request: false while no usable identity provider is set.
                signIn = new { configured = signIn.Current.IsConfigured },
                // From the same read: whether setup mode is open, so the admin UI goes to /setup.
                setup = new { open = Setup.SetupMode.IsOpen(signIn.Current.Values, time.GetUtcNow()) },
            });
        });
        app.MapPost(LogoutPath, (HttpContext http, ILoggerFactory loggers) =>
        {
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSignOut(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SourceAddress, actor.SessionId);
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();
        // Extends within the absolute limit; the session (and its sign-in time) stays the same, only its expiry moves.
        app.MapPost("/bff/session/extend", async (HttpContext http, TimeProvider time, IOptions<AdminOptions> admin, ILoggerFactory loggers) =>
        {
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = auth.Properties ?? new AuthenticationProperties();
            var maxSession = TimeSpan.FromHours(admin.Value.MaxSessionHours);
            if (AdminSessionLimit.Extend(properties, maxSession, time.GetUtcNow()) is not { } expires)
            {
                return Results.Problem(statusCode: 409, title: "The session cannot be extended",
                    detail: $"Sessions last at most {admin.Value.MaxSessionHours} hours. Save your work and sign in again.");
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, http.User, properties);
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSessionExtended(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SessionId, expires, actor.SourceAddress);
            return Results.Ok(new { sessionExpiresAt = expires, sessionLimitAt = AdminSessionLimit.LimitOf(properties, maxSession) });
        }).RequireAuthorization();
    }
}
