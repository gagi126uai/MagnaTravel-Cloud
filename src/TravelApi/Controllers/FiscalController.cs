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
    public async Task<IActionResult> Search(string q)
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
                _logger.LogInformation("Input looks like a DNI ({Dni}). Trying possible CUILs.", cleanQuery);
                var prefixes = new[] { "20", "27", "23" };
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

            // 3. Search by Name (always try this if it doesn't look like a FULL CUIT)
            _logger.LogInformation("Searching AFIP personas by name/query: {Query}", q);
            var cuitsByName = await FetchPersonasByName(q);
            if (cuitsByName != null && cuitsByName.Any())
            {
                // Fetch details for the first 5 unique CUITS we haven't found yet
                var detailTasks = cuitsByName
                    .Where(c => !foundCuits.Contains(c))
                    .Take(5)
                    .Select(async id => {
                        try {
                            return await FetchFromAfip(id);
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "Error fetching details for CUIT {Cuit} during search", id);
                            return null;
                        }
                    });
                
                var results = await Task.WhenAll(detailTasks);
                foreach (var r in results)
                {
                    if (r != null)
                    {
                        // Use reflection or dynamic to get 'Id' property from the anonymous object
                        var idProp = r.GetType().GetProperty("Id");
                        var idVal = idProp?.GetValue(r)?.ToString();
                        if (idVal != null && !foundCuits.Contains(idVal))
                        {
                            finalResults.Add(r);
                            foundCuits.Add(idVal);
                        }
                    }
                }
            }
            
            _logger.LogInformation("Fiscal search for '{Query}' returned {Count} results.", q, finalResults.Count);
            return Ok(finalResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Fiscal search for query {Query}", q);
            return StatusCode(500, $"Error interno en la búsqueda fiscal: {ex.Message}");
        }
    }

    private async Task<List<string>?> FetchPersonasByName(string name)
    {
        var url = $"https://soa.afip.gob.ar/sr-padron/v2/personas/{Uri.EscapeDataString(name)}";
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        
        if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;

        var data = doc.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) return null;

        var list = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            list.Add(item.GetString() ?? "");
        }
        return list;
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
        var taxConditionInfo = MapTaxCondition(data);

        return new
        {
            Id = id,
            Nombre = data.TryGetProperty("nombre", out var n) ? n.GetString() : null,
            Apellido = data.TryGetProperty("apellido", out var a) ? a.GetString() : null,
            RazonSocial = data.TryGetProperty("razonSocial", out var rs) ? rs.GetString() : null,
            TipoPersona = data.TryGetProperty("tipoPersona", out var tp) ? tp.GetString() : null,
            Estado = data.TryGetProperty("estado", out var e) ? e.GetString() : null,
            TaxCondition = taxConditionInfo.description,
            TaxConditionId = taxConditionInfo.id
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
