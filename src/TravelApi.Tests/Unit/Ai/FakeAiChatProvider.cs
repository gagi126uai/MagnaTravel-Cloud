using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Test double de <see cref="IAiChatProvider"/>. NO llama a la nube: devuelve resultados
/// pre-cargados, uno por invocacion (en orden). Asi podemos simular: respuesta valida,
/// JSON invalido seguido de uno valido (para probar el reintento), o degradacion.
///
/// <para>Tambien cuenta cuantas veces se lo invoco, para los tests que verifican que el
/// cerebro NO se llama cuando no corresponde (regresion del flag OFF) o que SE reintenta
/// exactamente una vez.</para>
/// </summary>
public sealed class FakeAiChatProvider : IAiChatProvider
{
    private readonly Queue<AiChatResult> _scriptedResults;

    /// <summary>Cantidad de veces que se llamo a <see cref="ChatAsync"/>.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Construye el fake con la secuencia de resultados que va a devolver, uno por llamada.
    /// Si se lo invoca mas veces que resultados cargados, devuelve un degradado generico
    /// (en vez de explotar), para que un test mal escrito falle por el assert, no por el fake.
    /// </summary>
    public FakeAiChatProvider(params AiChatResult[] scriptedResults)
    {
        _scriptedResults = new Queue<AiChatResult>(scriptedResults);
    }

    public Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        var result = _scriptedResults.Count > 0
            ? _scriptedResults.Dequeue()
            : AiChatResult.Degraded("fake sin mas resultados cargados");
        return Task.FromResult(result);
    }
}
