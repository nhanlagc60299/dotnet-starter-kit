using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Transfer.Services;

internal static class TenantIds
{
    public static string Current(IMultiTenantContextAccessor<AppTenantInfo> accessor) =>
        accessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException("No tenant resolved for this request.");
}
