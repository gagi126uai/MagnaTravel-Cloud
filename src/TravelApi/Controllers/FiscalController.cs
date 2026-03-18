using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/fiscal")]
[Authorize]
public class FiscalController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FiscalController> _logger;

    public FiscalController(HttpClient httpClient, ILogger<FiscalController> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
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
                    var result = await FetchFromAfip(cuil);
                    if (result != null) return Ok(result);
                }
                return NotFound("No se encontró información fiscal para este DNI en AFIP");
            }

            // Otherwise assume it's a CUIT/CUIL
            var data = await FetchFromAfip(cleanId);
            if (data == null) return NotFound("No se encontró información para el identificador proporcionado");
            
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Fiscal lookup");
            return StatusCode(500, "Error interno en la búsqueda fiscal");
        }
    }

    private async Task<object?> FetchFromAfip(string id)
    {
        var url = $"https://soa.afip.gob.ar/sr-padron/v2/persona/{id}";
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        
        if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;

        var data = doc.RootElement.GetProperty("data");
        return new
        {
            Id = id,
            Nombre = data.TryGetProperty("nombre", out var n) ? n.GetString() : null,
            Apellido = data.TryGetProperty("apellido", out var a) ? a.GetString() : null,
            RazonSocial = data.TryGetProperty("razonSocial", out var rs) ? rs.GetString() : null,
            TipoPersona = data.TryGetProperty("tipoPersona", out var tp) ? tp.GetString() : null,
            Estado = data.TryGetProperty("estado", out var e) ? e.GetString() : null,
            TaxCondition = MapTaxCondition(data)
        };
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
            if (prefix == "20") return CalculateCuil(dni, "23"); // Use 23 instead of 20
            if (prefix == "27") return CalculateCuil(dni, "23"); // Use 23 instead of 27
            // If already 23, it might need to handle specific logic, but 10 is rare
            checkDigit = 9; // Fallback/Error prev
        }

        return baseStr + checkDigit;
    }

    private string MapTaxCondition(JsonElement data)
    {
        if (data.TryGetProperty("monotributo", out var m) && m.ValueKind != JsonValueKind.Null) return "Monotributo";
        if (data.TryGetProperty("ganancias", out var g) && g.ValueKind != JsonValueKind.Null) return "Responsable Inscripto";
        return "Consumidor Final";
    }
}
