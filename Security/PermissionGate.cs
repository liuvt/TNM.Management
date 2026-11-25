using Microsoft.AspNetCore.Components.Authorization;

namespace TNM.Management.Security;

/// <summary>Live server-side RBAC check for each Blazor mutation (also invalidates revoked accounts).</summary>
public sealed class PermissionGate(AuthenticationStateProvider stateProvider, AuthRepository repository)
{
    public async Task<bool> CanAsync(string permission)
    {
        var state=await stateProvider.GetAuthenticationStateAsync();
        return repository.HasPermission(state.User, permission);
    }
    public async Task<long> RequireAsync(string permission)
    {
        var state=await stateProvider.GetAuthenticationStateAsync();
        if (!repository.HasPermission(state.User,permission))
            throw new UnauthorizedAccessException("Tài khoản không có quyền thực hiện thao tác hoặc phiên đăng nhập đã hết hiệu lực.");
        return AuthRepository.PrincipalId(state.User);
    }
    public async Task<bool> CanViewSystemLogsAsync()
    {
        var state = await stateProvider.GetAuthenticationStateAsync();
        return repository.CanViewSystemLogs(state.User);
    }
    public async Task<bool> CanViewRowLogsAsync()
    {
        var state = await stateProvider.GetAuthenticationStateAsync();
        return repository.CanViewRowLogs(state.User);
    }
    public async Task<long> RequireSystemLogsAsync()
    {
        var state = await stateProvider.GetAuthenticationStateAsync();
        if (!repository.CanViewSystemLogs(state.User))
            throw new UnauthorizedAccessException("Chỉ tài khoản Main, Chủ sở hữu (Owner) và Quản trị viên (Admin) mới được phép xem Lịch sử / Lưu vết / Log hệ thống.");
        return AuthRepository.PrincipalId(state.User);
    }
}
