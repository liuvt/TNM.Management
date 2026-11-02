using Microsoft.AspNetCore.Authorization;

namespace TNM.Management.Security;

/// <summary>Every policy decision checks SQLite and the current security stamp, not old cookie claims.</summary>
public sealed record LivePermissionRequirement(string PermissionKey) : IAuthorizationRequirement;

public sealed class LivePermissionHandler(AuthRepository accounts) : AuthorizationHandler<LivePermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,LivePermissionRequirement requirement)
    {
        if (accounts.HasPermission(context.User,requirement.PermissionKey)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
