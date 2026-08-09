using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.Ai;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Responde UNA pregunta: "¿con que datos hablo con la inteligencia artificial, ahora mismo?".
///
/// <para><b>Por que existe</b> (M-29 + M-33 de la spec firmada 2026-08-07): antes la conexion se
/// leia una sola vez al arrancar, de variables de entorno, y para cambiarla habia que reiniciar el
/// servidor. Ahora la configuracion se carga desde la pantalla, asi que hay que releerla, y ademas
/// hay que decidir quien manda cuando estan las dos (gana la de la pantalla).</para>
///
/// <para><b>M-30 — nada de datos viejos</b>: esta resolucion NO cachea. Cada llamada relee la fila
/// autoritativa de la base. Es una lectura de una fila por llamada a la IA (que ya es una llamada
/// por red de cientos de milisegundos), asi que el costo es despreciable, y a cambio desaparece la
/// clase entera de bugs del cache de AfipSettings: guardar una clave nueva y que el sistema siga
/// usando la vieja sin que nadie entienda por que. Si algun dia se agrega cache, hay que invalidarlo
/// SIEMPRE al guardar.</para>
/// </summary>
public interface IAiConnectionResolver
{
    /// <summary>
    /// Devuelve la configuracion efectiva, o <c>null</c> si no hay ninguna utilizable (ni en la
    /// base ni en el servidor). <c>null</c> NO es un error: significa "esta instalacion trabaja sin
    /// las ayudas inteligentes", que es un modo de funcionamiento valido (§3.5 de la spec).
    /// </summary>
    Task<AiConnectionResolution?> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// ¿Hay inteligencia artificial utilizable? Es la pregunta que REEMPLAZA al viejo interruptor
    /// <c>EnableAiCopilot</c> (M-33): ya no hay una llave aparte para prender la IA — si esta
    /// configurada, funciona; si no, no.
    /// </summary>
    Task<bool> IsUsableAsync(CancellationToken cancellationToken);
}
