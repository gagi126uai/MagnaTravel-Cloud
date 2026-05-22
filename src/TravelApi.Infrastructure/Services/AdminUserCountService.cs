using Microsoft.AspNetCore.Identity;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Identity;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.3 (ADR-009 §2.3.4.bis N-002, 2026-05-21): implementacion concreta
/// que usa <see cref="UserManager{ApplicationUser}"/> para contar admins activos.
///
/// <para><b>Por que string "Admin" hardcodeado y no <c>RolePolicies.Admin</c>
/// o similar</b>: el ADR es explicito (§2.3.4.bis) — el bypass GR-005 cuenta
/// el rol canonico "Admin" del <c>RoleSeeder</c>, NO permission strings.
/// Si en el futuro hay multi-tenant con roles distintos por agencia,
/// este service se especializa por tenant sin tocar el caller.</para>
///
/// <para><b>Performance</b>: <c>GetUsersInRoleAsync</c> hace JOIN entre
/// <c>AspNetUserRoles</c> y <c>AspNetUsers</c>. En un sistema con pocos
/// usuarios es trivial; si llegara a ser caro, se puede cachear (settings
/// no cambian por request) pero hoy no lo amerita.</para>
/// </summary>
public class AdminUserCountService : IAdminUserCountService
{
    // Mismo string canonico que usa el RoleSeeder y las RolePolicies del repo.
    // No usar "Administrator" ni "ADMIN" — el matching es case-sensitive en Identity.
    private const string AdminRoleName = "Admin";

    private readonly UserManager<ApplicationUser> _userManager;

    public AdminUserCountService(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    /// <inheritdoc />
    public async Task<int> CountActiveAdminsAsync(CancellationToken ct)
    {
        // GetUsersInRoleAsync NO acepta CancellationToken (limitacion de Identity 8.x).
        // El ct se respeta antes/despues; la query en si es rapida.
        ct.ThrowIfCancellationRequested();

        var admins = await _userManager.GetUsersInRoleAsync(AdminRoleName);

        // Filtramos por IsActive=true en memoria — la coleccion suele ser chica
        // (admins son pocos). Si crece, mover a query SQL.
        return admins.Count(u => u.IsActive);
    }
}
