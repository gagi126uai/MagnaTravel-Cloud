using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Con cual de las inteligencias artificiales de la calle trabaja esta instalacion.
///
/// <para>Es un valor INTERNO: se guarda como entero en la base y NUNCA viaja al navegador
/// (afuera viaja el codigo en texto del preset, ver <c>AiProviderPresets</c>). Regla P-17 de la
/// constitucion: nada tecnico llega al usuario.</para>
///
/// <para><b>Por que hay una lista cerrada y ademas <see cref="Other"/></b>: los seis primeros
/// traen direccion y modelo recomendados (el usuario solo pega la clave); <see cref="Other"/> es
/// la valvula de escape para un Llama propio o un proveedor que todavia no esta en la lista, y
/// obliga a cargar direccion y modelo a mano.</para>
/// </summary>
public enum AiProviderKey
{
    Groq = 0,
    OpenAi = 1,
    Anthropic = 2,
    Gemini = 3,
    Grok = 4,
    OpenRouter = 5,
    Other = 99,
}

/// <summary>
/// Resultado de la ultima prueba de conexion. El motivo viaja POR CODIGO (regla T-13): el front
/// elige la frase, nunca compara textos ni ve el mensaje crudo del proveedor.
/// </summary>
public enum AiConnectionTestOutcome
{
    /// <summary>Contesto bien. Es el unico caso "verde".</summary>
    Ok = 0,

    /// <summary>La clave no sirve o vencio (el proveedor devolvio 401/403).</summary>
    InvalidKey = 1,

    /// <summary>No hubo respuesta a tiempo (timeout, red caida, proveedor con problemas).</summary>
    NoResponse = 2,

    /// <summary>La direccion no sirve: no es https, esta mal escrita, o apunta a la red interna.</summary>
    InvalidAddress = 3,

    /// <summary>El modelo elegido no existe para ese proveedor.</summary>
    ModelNotFound = 4,
}

/// <summary>
/// Configuracion de la inteligencia artificial de ESTA instalacion (M-28 de la spec firmada
/// 2026-08-07 §15). Es una fila unica, igual que <see cref="OperationalFinanceSettings"/> o
/// <see cref="AfipSettings"/>.
///
/// <para><b>La clave es de una sola direccion</b>: entra cifrada (mismo mecanismo que ya protege
/// los datos sensibles de ARCA, <c>ISensitiveDataProtector</c>) y NO sale nunca. Lo unico que la
/// pantalla puede mostrar es <see cref="ApiKeyPrefix"/> (4 caracteres) para que el dueño reconozca
/// cual pego. No hay "ojito" ni boton de copiar: cambiarla es pegar una nueva encima.</para>
///
/// <para><b>Adenda a ADR-016 (derogacion del dueño, 2026-08-07)</b>: el ADR original decia que la
/// conexion vivia SOLO en variables de entorno y que la clave "nunca va a la DB". El dueño lo
/// derogo para este caso: el dueño de una agencia tiene que poder configurar su IA desde la
/// pantalla, sin un tecnico y sin tocar el servidor. Las variables de entorno <c>Ai__*</c> quedan
/// como RESPALDO para cuando esta tabla esta vacia (ver <c>AiConnectionResolver</c>).</para>
/// </summary>
public class AiSettings
{
    public int Id { get; set; }

    /// <summary>Con cual trabaja la agencia. Groq por default: es gratis para arrancar.</summary>
    public AiProviderKey Provider { get; set; } = AiProviderKey.Groq;

    /// <summary>
    /// Direccion del proveedor (la parte comun, sin el sufijo que agrega el motor al llamar).
    /// Se precarga sola desde el preset del proveedor elegido; el usuario solo la ve/toca si abre
    /// "Ajustes avanzados" o elige "Otra".
    /// </summary>
    [MaxLength(300)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Nombre del modelo. VOLATIL a proposito (los proveedores renombran sus modelos seguido),
    /// por eso es configurable y no una constante del codigo.
    /// </summary>
    [MaxLength(150)]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// La clave del proveedor, CIFRADA con <c>ISensitiveDataProtector</c> (AES-GCM, prefijo
    /// <c>enc:</c>). NUNCA se devuelve por la API, nunca se loguea, nunca se muestra.
    ///
    /// <para>El nombre de esta propiedad esta ademas en la lista de campos que la auditoria
    /// automatica de <c>AppDbContext</c> NO copia al historial (<c>SensitiveAuditFields</c>), para
    /// que ni siquiera el texto cifrado quede duplicado en la tabla de auditoria.</para>
    /// </summary>
    [MaxLength(4000)]
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// Los primeros 4 caracteres de la clave, EN CLARO. Es lo unico mostrable ("empieza con
    /// gsk_…") y alcanza para que el dueño reconozca cual clave pego, sin poder reconstruirla.
    /// </summary>
    [MaxLength(8)]
    public string? ApiKeyPrefix { get; set; }

    /// <summary>Como le fue a la ultima prueba. Null = todavia nadie probo.</summary>
    public AiConnectionTestOutcome? LastTestOutcome { get; set; }

    /// <summary>Cuando fue esa ultima prueba (UTC). Null = todavia nadie probo.</summary>
    public DateTime? LastTestAt { get; set; }

    /// <summary>Quien toco esta configuracion por ultima vez (Id del usuario, uso interno).</summary>
    [MaxLength(450)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>Nombre visible de quien la toco por ultima vez (es lo que se puede mostrar).</summary>
    [MaxLength(200)]
    public string? UpdatedByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hay clave guardada en la base. Se usa para decidir si la configuracion de la base es
    /// COMPLETA (y por lo tanto le gana al respaldo del servidor, M-29).
    /// </summary>
    public bool HasStoredApiKey() => !string.IsNullOrWhiteSpace(EncryptedApiKey);

    /// <summary>
    /// La configuracion guardada alcanza para hablar con el proveedor: hacen falta las tres cosas
    /// (direccion, modelo y clave). Con una sola que falte NO se usa y manda el respaldo del
    /// servidor, porque mezclar media configuracion de la base con media del servidor daria una
    /// combinacion que no funciona (la clave de uno con la direccion de otro).
    /// </summary>
    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Model)
        && HasStoredApiKey();
}
