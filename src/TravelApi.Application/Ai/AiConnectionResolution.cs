namespace TravelApi.Application.Ai;

/// <summary>De donde salio la configuracion que se esta usando para hablar con la IA.</summary>
public enum AiConfigurationSource
{
    /// <summary>No hay configuracion utilizable en ningun lado: las ayudas inteligentes no corren.</summary>
    None = 0,

    /// <summary>La cargo el dueño desde la pantalla de Configuracion. MANDA sobre el servidor (M-29).</summary>
    Database = 1,

    /// <summary>La dejo el tecnico en las variables del servidor (<c>Ai__*</c>). Es el RESPALDO.</summary>
    Environment = 2,
}

/// <summary>
/// La configuracion EFECTIVA de la IA en este momento: con que datos hablar y de donde salieron.
///
/// <para><b>Precedencia (M-29, adenda firmada a ADR-016 del 2026-08-07)</b>: primero lo guardado en
/// la base; si ahi no hay una configuracion completa, se usan las variables de entorno como
/// respaldo; si tampoco, no hay IA y todo funciona igual, sin las ayudas.</para>
/// </summary>
/// <param name="Options">Los datos de conexion listos para usar (con la clave ya descifrada).</param>
/// <param name="Source">De donde salieron. Nunca <see cref="AiConfigurationSource.None"/> aca.</param>
public sealed record AiConnectionResolution(AiConnectionOptions Options, AiConfigurationSource Source);
