using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// Refuses the request when password login is disabled because users log in with OpenID Connect.
/// </summary>
/// <remarks>
/// Applied to every endpoint that logs in with, changes or recovers a password. Without it a user created by
/// OpenID Connect, who has no password, could be logged in to with an empty password.
/// </remarks>
public sealed class PasswordLoginAttribute : ActionFilterAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var settings = context.HttpContext.RequestServices.GetService<OidcSettings>();
        if (settings is not null && !settings.PasswordLoginEnabled)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Password login is disabled.",
                Detail = "Users log in with single sign-on or Quick Connect, and have no password to use or change."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        base.OnActionExecuting(context);
    }
}
