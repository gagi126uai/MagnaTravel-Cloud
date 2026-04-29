using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

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

    [HttpGet("api/reservas/{reservaPublicIdOrLegacyId}/vouchers")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/reservas/{reservaPublicIdOrLegacyId}/vouchers/external")]
    [EnableRateLimiting("uploads")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/issue")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/approve")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/reject")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/ensure-send")]
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
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/vouchers/{voucherPublicIdOrLegacyId}/sent")]
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
    }

    [HttpGet("api/vouchers/{voucherPublicIdOrLegacyId}/download")]
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
