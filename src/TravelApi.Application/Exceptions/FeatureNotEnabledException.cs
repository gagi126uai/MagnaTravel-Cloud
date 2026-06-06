namespace TravelApi.Application.Exceptions;

/// <summary>
/// Se lanza cuando se invoca una operacion que vive detras de un feature flag APAGADO. Los controllers
/// la traducen a <c>404 Not Found</c>: con el flag OFF el endpoint "no existe" (indistinguible de una ruta
/// inexistente), igual que el resto de los endpoints gateados por flag del proyecto (ADR-017 §2.3).
/// </summary>
public class FeatureNotEnabledException : Exception
{
    public FeatureNotEnabledException(string message) : base(message) { }
}
