using System.Threading;
using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Actor is bound to the currently executing command, not a shared singleton.
/// Always call As(actorId) after verifying authorization, before opening a writer connection.
/// Unattributed background/legacy commands remain SYSTEM (NULL), never impersonate Owner.
/// </summary>
public static class AuditRuntime
{
    private static readonly AsyncLocal<long?> Current = new();
    private static readonly AsyncLocal<int> InternalSyncDepth = new();
    public static long? ActorId => Current.Value is > 0 ? Current.Value : null;
    public static bool IsInternalSync => InternalSyncDepth.Value > 0;

    public static IDisposable As(long actorId)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException("Audit yêu cầu người thao tác hợp lệ.");
        var prior = Current.Value;
        Current.Value = actorId;
        return new Restore(() => Current.Value = prior);
    }

    /// <summary>
    /// Cho phép các nghiệp vụ đồng bộ liên phân hệ đã được phân quyền (VD: Owner chuyển đơn vị Cách 1,
    /// cập nhật / xóa bớt 1 xe trong đơn gộp nhiều xe, đồng bộ Đơn gia hạn GH7) cập nhật đồng bộ bảng v7_orders & v7_order_lines.
    /// </summary>
    public static IDisposable AllowCompletedOrderSync()
    {
        var prior = InternalSyncDepth.Value;
        InternalSyncDepth.Value = prior + 1;
        return new Restore(() => InternalSyncDepth.Value = prior);
    }

    public static void Attach(SqliteConnection connection, Func<long?>? suppliedActor = null)
    {
        connection.CreateFunction<long?>("tnm_actor", () => suppliedActor?.Invoke() is > 0
            ? suppliedActor.Invoke() : ActorId, isDeterministic: false);
        connection.CreateFunction<long>("tnm_internal_sync", () => IsInternalSync ? 1L : 0L, isDeterministic: false);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; restore(); } }
    }
}
