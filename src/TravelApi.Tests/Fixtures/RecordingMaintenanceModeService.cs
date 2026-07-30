using TravelApi.Application.Interfaces;

namespace TravelApi.Tests.Fixtures;

/// <summary>
/// Fake de <see cref="IMaintenanceModeService"/> para tests: en vez de solo simular el flag, GRABA el orden en
/// que se llamó cada método (<see cref="Calls"/>) — así los tests de "Restaurar TOTAL" pueden verificar que el
/// mantenimiento se activa ANTES del <c>pg_restore</c> y se desactiva DESPUÉS (incluso si el restore falla),
/// sin depender de un archivo en disco real (a diferencia de <c>FileMaintenanceModeService</c>, que sí lo usa).
/// </summary>
public sealed class RecordingMaintenanceModeService : IMaintenanceModeService
{
    public List<string> Calls { get; } = new();

    public bool IsActive { get; private set; }

    public string? Reason { get; private set; }

    public DateTime? SinceUtc { get; private set; }

    public string? CurrentStep { get; private set; }

    /// <summary>
    /// Todos los pasos publicados, EN ORDEN: así el test puede verificar la secuencia, no solo el último. Van
    /// en una lista APARTE de <see cref="Calls"/> a propósito: <see cref="Calls"/> es el ciclo de vida del
    /// mantenimiento (activar/renovar/desactivar) y varios tests fijan su secuencia EXACTA — publicar un paso
    /// no es un evento de ese ciclo de vida y no tiene por qué romperlos.
    /// </summary>
    public List<string> PublishedSteps { get; } = new();

    public void SetStep(string step)
    {
        if (!IsActive)
        {
            return;
        }

        CurrentStep = step;
        PublishedSteps.Add(step);
    }

    /// <summary>Hallazgo B-N2(a): si true, esta sesión está exenta de la auto-expiración (nunca aplica en este fake, que no la simula — solo se graba para que los tests verifiquen que se pidió).</summary>
    public bool RequiresManualClear { get; private set; }

    public bool TryActivate(string reason)
    {
        if (IsActive)
        {
            Calls.Add("TryActivate:false");
            return false;
        }

        IsActive = true;
        Reason = reason;
        SinceUtc = DateTime.UtcNow;
        RequiresManualClear = false;
        Calls.Add("Activate");
        return true;
    }

    public void Touch()
    {
        if (!IsActive)
        {
            return;
        }

        SinceUtc = DateTime.UtcNow;
        Calls.Add(nameof(Touch));
    }

    public void SuppressAutoExpiry(string reason)
    {
        if (!IsActive)
        {
            return;
        }

        Reason = reason;
        RequiresManualClear = true;
        CurrentStep = null;
        Calls.Add(nameof(SuppressAutoExpiry));
    }

    public void Deactivate()
    {
        IsActive = false;
        Reason = null;
        SinceUtc = null;
        RequiresManualClear = false;
        CurrentStep = null;
        Calls.Add(nameof(Deactivate));
    }
}
