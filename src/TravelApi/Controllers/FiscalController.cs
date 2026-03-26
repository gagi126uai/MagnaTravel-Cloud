using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/fiscal")]
[Authorize(Roles = "Admin")]
public class FiscalController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FiscalController> _logger;
    private readonly IAfipService _afipService;

    public FiscalController(HttpClient httpClient, ILogger<FiscalController> logger, IAfipService afipService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _afipService = afipService;
    }

    [HttpGet("persona/{id}")]
    public async Task<IActionResult> GetPersona(string id)
    {
        try
        {
            var cleanId = id.Replace("-", "").Replace(".", "").Trim();
            
            // If it's a DNI (7-8 digits), try possible CUILs
            if (cleanId.Length >= 7 && cleanId.Length <= 8)
            {
                var prefixes = new[] { "20", "27", "23" };
                foreach (var prefix in prefixes)
                {
                    var cuil = CalculateCuil(cleanId, prefix);
                    var resultByDni = await FetchFromAfip(cuil);
                    if (resultByDni != null) return Ok(resultByDni);
                }
                return NotFound("No se encontró información fiscal para este DNI en AFIP");
            }

            // Otherwise assume it's a CUIT/CUIL
            var directData = await FetchFromAfip(cleanId);
            if (directData == null) return NotFound("No se encontró información para el identificador proporcionado");
            
            return Ok(directData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Fiscal lookup");
            return StatusCode(500, "Error interno en la búsqueda fiscal");
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(string q, [FromQuery] string? gender = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q)) return BadRequest("Consulta vacía");
            var cleanQuery = q.Replace("-", "").Replace(".", "").Trim();

            var finalResults = new List<object>();
            var foundCuits = new HashSet<string>();

            // 1. If it's pure numeric and 11 chars, it's a CUIT. 
            if (cleanQuery.Length == 11 && long.TryParse(cleanQuery, out _))
            {
                var data = await FetchFromAfip(cleanQuery);
                if (data != null) return Ok(new List<object> { data });
                return Ok(new List<object>());
            }

            // 2. If it's numeric and 7-8 chars, it's likely a DNI.
            if ((cleanQuery.Length == 7 || cleanQuery.Length == 8) && long.TryParse(cleanQuery, out _))
            {
                _logger.LogInformation("Input looks like a DNI ({Dni}) with Gender {Gender}. Trying possible CUILs.", cleanQuery, gender ?? "N/A");
                
                var prefixes = new List<string>();
                if (gender?.ToUpper() == "M") prefixes.AddRange(new[] { "20", "23", "24", "27" });
                else if (gender?.ToUpper() == "F") prefixes.AddRange(new[] { "27", "23", "24", "20" });
                else prefixes.AddRange(new[] { "20", "27", "23", "24" });

                foreach (var prefix in prefixes)
                {
                    var cuil = CalculateCuil(cleanQuery, prefix);
                    if (foundCuits.Contains(cuil)) continue;
                    
                    var data = await FetchFromAfip(cuil);
                    if (data != null)
                    {
                        finalResults.Add(data);
                        foundCuits.Add(cuil);
                    }
                }
            }

            // 3. Search by Name (Disabled for official Padron A5)
            // The official AFIP ws_sr_padron_a5 only supports exact CUIT searches.
            // Name searching requires a paid third-party service like Nosis.
            
            _logger.LogInformation("Fiscal search for '{Query}' returned {Count} results.", q, finalResults.Count);
            return Ok(finalResults);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AFIP search failed for query {Query}. Reason: {Msg}", q, ex.Message);
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "La consulta fiscal no pudo completarse.");
        }
    }

    private async Task<object?> FetchFromAfip(string id)
    {
        if (long.TryParse(id, out long cuit))
        {
            return await _afipService.GetPersonaDetailsAsync(cuit);
        }
        return null;
    }

    private string CalculateCuil(string dni, string prefix)
    {
        // Pad DNI to 8 digits
        dni = dni.PadLeft(8, '0');
        var baseStr = prefix + dni;
        int[] multipliers = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };
        int sum = 0;
        
        for (int i = 0; i < 10; i++)
        {
            sum += (baseStr[i] - '0') * multipliers[i];
        }

        int remainder = sum % 11;
        int checkDigit = 11 - remainder;

        if (checkDigit == 11) checkDigit = 0;
        if (checkDigit == 10)
        {
            // Special cases (Prefix 23)
            if (prefix == "20") return CalculateCuil(dni, "23"); 
            if (prefix == "27") return CalculateCuil(dni, "23"); 
            checkDigit = 9; 
        }

        return baseStr + checkDigit;
    }

    private (string description, int id) MapTaxCondition(JsonElement data)
    {
        // Check for Monotributo
        if (data.TryGetProperty("monotributo", out var m) && m.ValueKind != JsonValueKind.Null && m.ValueKind != JsonValueKind.String)
        {
             return ("Monotributo", 6);
        }
        
        // Check for Ganancias (Responsable Inscripto)
        if (data.TryGetProperty("ganancias", out var g) && g.ValueKind != JsonValueKind.Null && g.ValueKind != JsonValueKind.String)
        {
             return ("Responsable Inscripto", 1);
        }

        // Check for IVA (if available in some patterns)
        if (data.TryGetProperty("iva", out var iva) && iva.ValueKind == JsonValueKind.String)
        {
            var ivaStr = iva.GetString()?.ToLower() ?? "";
            if (ivaStr.Contains("exento")) return ("Exento", 4);
            if (ivaStr.Contains("inscripto")) return ("Responsable Inscripto", 1);
            if (ivaStr.Contains("monotributo")) return ("Monotributo", 6);
        }

        return ("Consumidor Final", 5);
    }
}
