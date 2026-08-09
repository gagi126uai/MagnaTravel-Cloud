namespace TravelApi.Application.Ai;

/// <summary>
/// Los ajustes tecnicos de "la linea inteligente". Son del servidor, no del dueño de la agencia
/// (P-15: no se le pregunta lo que no tiene por que decidir).
/// </summary>
public sealed class ServiceLineInterpretationOptions
{
    /// <summary>
    /// Cuanto se espera al modelo antes de dar la frase por no entendida.
    ///
    /// <para><b>Por que es CORTO y aparte del timeout general de la IA</b>: el vendedor esta parado
    /// frente a la ficha esperando que aparezca el amarillo. Si el modelo tarda mas que esto, la
    /// respuesta llega tarde para servir de algo: es mejor cortar y dejar el buscador de siempre
    /// (§3.5, "si el sistema tarda, la caja se comporta como el buscador de siempre") que dejarlo
    /// mirando el "Buscando…" 15 segundos.</para>
    /// </summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>Piso y techo del tiempo de espera, para que una configuracion rara no lo deje en 0 ni en 5 minutos.</summary>
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 30;
}
