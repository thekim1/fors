using Fors.AdminApi.Budgets;
using Fors.AdminApi.Catalog;
using Fors.AdminApi.Common;
using Fors.AdminApi.DataAccess;
using Fors.AdminApi.Integrations;
using Fors.AdminApi.Keys;
using Fors.AdminApi.Operations;
using Fors.AdminApi.Organisation;
using Fors.AdminApi.Reports;
using Fors.AdminApi.RoutingRules;
using Fors.AdminApi.Security;
using Fors.AdminApi.Settings;

namespace Fors.AdminApi;

public static class AdminEndpoints
{
    /// <summary>
    /// <c>/api</c>: every feature's endpoints, for signed-in users only, rate limited per user, with <see cref="AdminRequest"/>
    /// bodies validated before the handler runs. Each feature adds the role it needs.
    /// </summary>
    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting(AdminPolicies.PerUserRateLimit).AddEndpointFilter<AdminValidationFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        api.MapOrganisation();
        api.MapKeys();
        api.MapIntegrations();
        api.MapCatalog();
        api.MapBudgets();
        api.MapSettings();
        api.MapRoutingRules();
        api.MapReports();
        api.MapOperations();
        api.MapDataAccess();
    }
}
