using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): se lanza cuando el borrado masivo NO puede ejecutarse (frase que no
/// coincide, contraseña incorrecta, candado fiscal activo, o el backup previo falló). El mensaje YA viene en
/// criollo, listo para mostrar al usuario tal cual — el controller lo traduce a 409 sin reprocesarlo.
/// </summary>
public sealed class SystemDataWipeRefusedException : Exception
{
    public SystemDataWipeRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): borrado seguro de TODOS los datos de negocio cargados (reservas,
/// clientes, proveedores, facturas, catálogo, etc.), con backup previo obligatorio y candado fiscal. Ver el
/// diseño completo en la implementación (<c>SystemDataWipeService</c>, Infrastructure).
/// </summary>
public interface ISystemDataWipeService
{
    /// <summary>Solo lectura: conteos actuales + si el candado fiscal está activo + nombre estimado del backup.</summary>
    Task<SystemDataWipePreviewResponse> GetPreviewAsync(CancellationToken ct);

    /// <summary>
    /// Ejecuta el borrado real de los grupos pedidos (Parte A, 2026-07-27: borrado selectivo por grupos — ver
    /// <c>TravelApi.Application.Constants.WipeGroups</c>). Tira <see cref="SystemDataWipeRefusedException"/> si
    /// la frase no coincide, la contraseña es incorrecta, algún grupo no existe o le falta un grupo dependiente
    /// (regla "tilda solo y avisa"), el candado fiscal está activo (solo aplica si <c>reservasYPlata</c> está
    /// entre los grupos pedidos), o el backup previo falló — en todos esos casos NO se borra nada.
    /// </summary>
    Task<SystemDataWipeResponse> ExecuteWipeAsync(
        string requesterUserId,
        string password,
        string phrase,
        IReadOnlyList<string> grupos,
        CancellationToken ct);
}
