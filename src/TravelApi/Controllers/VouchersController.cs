using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Authorize]
public class VouchersController : ControllerBase
{
    private readonly IVoucherService _voucherService;
    private readonly ILogger<VouchersController> _logger;

    public VouchersController(IVoucherService voucherService, ILogger<VouchersController> logger)
    {
        _voucherService = voucherService;
        _logger = logger;
    }

    // B1.15 Fase 0' (CODE-08): hotfix de seguridad. Antes este controller era
    // [Authorize] sin permission ni ownership: cualquier autenticado podia ver,
    // generar, anular o subir vouchers de cualquier reserva. Critico — los
    // vouchers tienen datos del pasajero, codigo de confirmacion y archivo PDF.
    //
    // Granularidad de permisos:
    //  - VouchersGenerate: ver listas + generar voucher + ver/descargar PDF.
    //  - VouchersUpload: subir voucher externo (operadores que mandan PDF).
    //  - VouchersIssue + VouchersAuthorizeException: emitir + aprobar/rechazar.
    //  - VouchersSend: registrar envio + ensure-send.
    //  - VouchersRevoke: revocar.
    [HttpGet("api/reservas/{reservaPublicIdOrLegacyId}/vouchers")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<IReadOnlyList<VoucherDto>>> GetReservaVouchers(
        string reservaPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var vouchers = await _voucherService.GetVouchersAsync(reservaPublicIdOrLegacyId, cancellationToken);
            return Ok(vouchers);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("api/reservas/{reservaPublicIdOrLegacyId}/vouchers/generate")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> GenerateVoucher(
        string reservaPublicIdOrLegacyId,
        [FromBody] GenerateVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.GenerateVoucherRecordAsync(
                reservaPublicIdOrLegacyId,
                request,
                BuildActor(),
                cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/reservas/{reservaPublicIdOrLegacyId}/vouchers/external")]
    [EnableRateLimiting("uploads")]
    [RequirePermission(Permissions.VouchersUpload)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> UploadExternalVoucher(
        string reservaPublicIdOrLegacyId,
        [FromForm] UploadExternalVoucherForm form,
        CancellationToken cancellationToken)
    {
        if (form.File is null || form.File.Length == 0)
        {
            return BadRequest(new { message = "Debe seleccionar un archivo de voucher." });
        }

        try
        {
            await using var stream = form.File.OpenReadStream();
            var voucher = await _voucherService.UploadExternalVoucherAsync(
                reservaPublicIdOrLegacyId,
                new UploadExternalVoucherRequest
                {
                    Scope = form.Scope,
                    PassengerIds = form.PassengerIds ?? new List<string>(),
                    ExternalOrigin = form.ExternalOrigin
                },
                stream,
                form.File.FileName,
                form.File.ContentType,
                form.File.Length,
                BuildActor(),
                cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/issue")]
    [RequirePermission(Permissions.VouchersIssue)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> IssueVoucher(
        string voucherPublicIdOrLegacyId,
        [FromBody] IssueVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.IssueVoucherAsync(voucherPublicIdOrLegacyId, request, BuildActor(), cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/approve")]
    [RequirePermission(Permissions.VouchersAuthorizeException)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> ApproveVoucher(
        string voucherPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.ApproveVoucherIssueAsync(voucherPublicIdOrLegacyId, BuildActor(), cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/reject")]
    [RequirePermission(Permissions.VouchersAuthorizeException)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> RejectVoucher(
        string voucherPublicIdOrLegacyId,
        [FromBody] RejectVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.RejectVoucherIssueAsync(voucherPublicIdOrLegacyId, request, BuildActor(), cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/revoke")]
    [RequirePermission(Permissions.VouchersRevoke)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> RevokeVoucher(
        string voucherPublicIdOrLegacyId,
        [FromBody] RevokeVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.RevokeVoucherAsync(voucherPublicIdOrLegacyId, request, BuildActor(), cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/ensure-send")]
    [RequirePermission(Permissions.VouchersSend)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> EnsureVoucherCanBeSent(
        string voucherPublicIdOrLegacyId,
        [FromBody] EnsureVoucherSendRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var voucher = await _voucherService.EnsureVoucherCanBeSentAsync(
                request.ReservaId,
                voucherPublicIdOrLegacyId,
                request.PassengerId,
                request.Exception,
                BuildActor(),
                cancellationToken);
            return Ok(voucher);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/sent")]
    [RequirePermission(Permissions.VouchersSend)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<VoucherDto>> RecordVoucherSent(
        string voucherPublicIdOrLegacyId,
        [FromBody] RecordVoucherSentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _voucherService.RecordVoucherSentAsync(voucherPublicIdOrLegacyId, BuildActor(), request.Reason, cancellationToken);
            return Ok(new VoucherDto
            {
                PublicId = Guid.TryParse(voucherPublicIdOrLegacyId, out var publicId) ? publicId : Guid.Empty
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = NormalizeProxyMessage(ex.Message) });
        }
    }

    [HttpGet("api/vouchers/{voucherPublicIdOrLegacyId}/download")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Voucher, "voucherPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> DownloadVoucher(string voucherPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var file = await _voucherService.DownloadVoucherAsync(voucherPublicIdOrLegacyId, cancellationToken);
            return File(file.Bytes, file.ContentType, file.FileName);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Voucher file missing for {VoucherId}", voucherPublicIdOrLegacyId);
            return NotFound();
        }
    }

    private OperationActor BuildActor()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(role => role.Value).Where(role => !string.IsNullOrWhiteSpace(role)).ToArray();
        return new OperationActor(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System",
            User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Sistema",
            roles);
    }

    private static string NormalizeProxyMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No se pudo procesar la solicitud.";
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var messageProperty) &&
                messageProperty.ValueKind == JsonValueKind.String)
            {
                return messageProperty.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // The message is already plain text.
        }

        return message;
    }
}

public class UploadExternalVoucherForm
{
    public IFormFile? File { get; set; }
    public string Scope { get; set; } = "ReservaCompleta";
    public List<string>? PassengerIds { get; set; }
    public string ExternalOrigin { get; set; } = "Operador externo";
}

public class RecordVoucherSentRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
