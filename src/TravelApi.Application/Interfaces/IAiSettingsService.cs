using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// La pantalla "Configuracion → Inteligencia artificial" (spec firmada 2026-08-07 §15), del lado
/// del motor. Solo Admin llega hasta aca (lo gatea el controller).
/// </summary>
public interface IAiSettingsService
{
    /// <summary>
    /// La foto de la configuracion. <b>Jamas incluye la clave</b>: devuelve si hay clave, de donde
    /// sale y sus primeros 4 caracteres (M-28, write-only).
    /// </summary>
    Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Guarda proveedor, direccion, modelo y (si vino una nueva) la clave, cifrada. Si el pedido no
    /// trae clave y ya habia una guardada, se conserva la que estaba.
    /// </summary>
    /// <param name="updatedByUserId">Id del usuario que guarda (auditoria: quien y cuando).</param>
    /// <param name="updatedByUserName">Nombre visible del usuario que guarda.</param>
    Task<AiSettingsDto> UpdateAsync(
        UpdateAiSettingsRequest request,
        string? updatedByUserId,
        string? updatedByUserName,
        CancellationToken cancellationToken);

    /// <summary>
    /// La lista de proveedores con su bajada y sus valores recomendados (M-32). Sale del motor para
    /// que sumar un proveedor manana no obligue a tocar la pantalla.
    /// </summary>
    AiProviderPresetsResponse GetProviderPresets();

    /// <summary>
    /// Prueba lo que viene en el pedido, este guardado o no. Si el pedido no trae clave pero hay
    /// una guardada, prueba con la guardada. <b>Probar NO guarda</b> — salvo el resultado de la
    /// prueba en si, que queda registrado para la foto de estado (§15.5).
    /// </summary>
    Task<AiConnectionTestResultDto> TestConnectionAsync(
        TestAiConnectionRequest request,
        CancellationToken cancellationToken);
}
