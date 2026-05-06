using Microsoft.AspNetCore.Identity;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Identity;

/// <summary>
/// Modelo de usuario de ASP.NET Identity. Vive en Infrastructure (no en Domain)
/// porque acopla con Microsoft.AspNetCore.Identity.EntityFrameworkCore.
/// Las entidades del dominio (p.ej. Reserva, RefreshToken) referencian al usuario
/// por su Id (string) sin nav prop, manteniendo el dominio independiente de Identity.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
