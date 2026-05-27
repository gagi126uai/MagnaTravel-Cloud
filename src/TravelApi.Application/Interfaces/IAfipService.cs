using TravelApi.Domain.Entities;
using TravelApi.Domain.Entities.Afip;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IAfipService
{
    // Configuration
    Task<bool> ValidateCertificate(byte[] certData, string password);
    Task<string> GetStatus();
    Task<AfipSettings?> GetSettingsAsync();
    Task<AfipSettings> UpdateSettingsAsync(long cuit, int puntoDeVenta, bool isProduction, string taxCondition, 
        byte[]? certificateData, string? certificateFileName, string? password,
        byte[]? prodCertificateData, string? prodCertificateFileName, string? prodPassword);

    // Core
    Task<Invoice> CreatePendingInvoice(int ReservaId, CreateInvoiceRequest request);
    Task ProcessInvoiceJob(int invoiceId);
    Task<AfipVoucherDetails?> GetVoucherDetails(int cbteTipo, int ptoVta, long cbteNro);
    Task<object?> GetPersonaDetailsAsync(long cuit);

    // ---- FC1.3.F2.2 (RH3-001 / RH3-004 round 4 Camino A, 2026-05-27): CONSULTA a ARCA ----
    // Estos dos metodos SOLO leen de ARCA (FECompUltimoAutorizado + FECompConsultar).
    // NO emiten comprobantes. Alimentan el "stale key recovery" anti-duplicados del job
    // de NC parcial (Etapa 5), que necesita preguntarle a ARCA "¿cual fue el ultimo
    // comprobante autorizado?" para decidir si un reintento de Hangfire debe re-emitir
    // o derivar el CAE de un comprobante ya emitido.

    /// <summary>
    /// FC1.3.F2.2 (sub-tarea A.7.0, RH3-004 round 4): devuelve el ultimo numero de
    /// comprobante autorizado por ARCA para el punto de venta + tipo dados
    /// (internamente <c>GetNextVoucherNumber - 1</c>).
    ///
    /// <para><b>Por que existe</b>: el numerador interno <c>GetNextVoucherNumber</c> es
    /// <c>private</c> y exige <c>AfipSettings</c> como parametro, exponiendo una
    /// dependencia interna al caller. Este helper encapsula la carga de settings + auth
    /// adentro del servicio, asi el job de NC parcial puede capturar el snapshot del
    /// numerador SIN tocar internals de AfipService.</para>
    /// </summary>
    Task<int> GetLastAuthorizedNumeroAsync(int puntoVenta, int cbteTipo, CancellationToken ct);

    /// <summary>
    /// FC1.3.F2.2 (sub-tareas A.2 / A.3, RH3-001 round 4): consulta compuesta a ARCA
    /// para el stale key recovery de la NC parcial.
    ///
    /// <para>Compara el ultimo numero autorizado por ARCA contra
    /// <paramref name="lastSeenNumeroBeforePost"/> (el snapshot que el job tomo ANTES de
    /// postear). Si el numerador NO avanzo, el POST nunca viajo y devuelve
    /// <c>Found=false</c>. Si avanzo, trae el detalle del ultimo comprobante via
    /// <c>GetVoucherDetails</c> y arma el resultado con <c>Found=true</c>.</para>
    ///
    /// <para><b>Importante</b>: este metodo NO decide si re-emitir. Solo informa. La
    /// decision (derivar CAE vs. borrar key + reintentar) la toma el job comparando
    /// <see cref="ArcaCompoundQueryResult.CbteAsoc"/> y
    /// <see cref="ArcaCompoundQueryResult.ImporteTotal"/> contra lo esperado.</para>
    /// </summary>
    Task<ArcaCompoundQueryResult> QueryLastAuthorizedWithDetailsAsync(
        int puntoVenta,
        int cbteTipo,
        int? lastSeenNumeroBeforePost,
        CancellationToken ct);
}
