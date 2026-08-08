namespace TravelApi.Domain.Exceptions;

/// <summary>
/// No existe el producto del tarifario que se pidió.
///
/// <para><b>Por qué no se usa <see cref="System.Collections.Generic.KeyNotFoundException"/></b>: esa es
/// una excepción del framework y su mensaje lo escribe .NET, no nosotros. Si el controller devolviera su
/// texto, al usuario le podría llegar algo en inglés o con nombres internos. Con una excepción propia, el
/// <c>Message</c> es SIEMPRE una frase nuestra, en criollo, pensada para que la lea una persona.</para>
/// </summary>
public sealed class CatalogProductNotFoundException : Exception
{
    public CatalogProductNotFoundException(string message) : base(message) { }
}

/// <summary>No existe el movimiento del bibliotecario que se quiso ver o deshacer. Mismo criterio de arriba.</summary>
public sealed class CatalogTidyUpNotFoundException : Exception
{
    public CatalogTidyUpNotFoundException(string message) : base(message) { }
}

/// <summary>
/// La unión existe pero YA NO se puede deshacer con fidelidad (hubo ventas nuevas encima, o la memoria de
/// precios que tocaba ya no está).
///
/// <para>Es un CONFLICTO con el estado actual (409), no un dato mal escrito: no hay nada que el usuario
/// pueda corregir y reintentar. El <c>Message</c> explica en criollo por qué, y es lo que se muestra.</para>
/// </summary>
public sealed class CatalogTidyUpNotReversibleException : InvalidOperationException
{
    public CatalogTidyUpNotReversibleException(string message) : base(message) { }
}
