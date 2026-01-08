namespace TravelApi.Services;

public class CupoNotFoundException : Exception
{
    public CupoNotFoundException(int cupoId)
        : base($"El cupo {cupoId} no existe.")
    {
    }
}

public class CupoOverbookingException : Exception
{
    public CupoOverbookingException(string message)
        : base(message)
    {
    }
}

public class CupoConcurrencyException : Exception
{
    public CupoConcurrencyException()
        : base("No se pudo confirmar el cupo por una actualización concurrente.")
    {
    }
}
