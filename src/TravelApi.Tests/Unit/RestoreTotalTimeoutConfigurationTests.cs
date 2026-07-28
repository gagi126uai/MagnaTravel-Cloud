using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B-N2(d) de seguridad, "los timeouts no cierran
/// entre sí"): test de GUARDIA que fija la invariante entre los timeouts de cada paso de una restauración
/// total y <c>Maintenance:MaxDurationMinutes</c> (el presupuesto de auto-expiración del modo mantenimiento).
///
/// <para><b>Por qué DOS caminos, no uno solo</b>: <c>SystemDataRestoreService.ExecuteTotalRestoreAsync</c>
/// llama a <c>IMaintenanceModeService.Touch()</c> justo ANTES de arrancar el <c>pg_restore</c> real — desde
/// ese punto, el presupuesto de auto-expiración mide SOLO el tiempo del <c>pg_restore</c> (camino 2). Pero los
/// pasos ANTERIORES (chequeo de esquema + backup previo + copia de MinIO) corren bajo el reloj SIN resetear
/// (arrancado en <c>TryActivate</c>) — si sumados excedieran <c>MaxDurationMinutes</c>, la auto-expiración
/// podría dispararse MIENTRAS esos pasos legítimos todavía están en curso (camino 1). Ambos caminos tienen que
/// caber, con margen, dentro del mismo presupuesto.</para>
///
/// <para>Los números vienen de constantes <c>internal</c> expuestas en cada clase (nunca duplicados a mano
/// acá) — si alguien cambia un default sin actualizar el otro, este test lo detecta.</para>
/// </summary>
public class RestoreTotalTimeoutConfigurationTests
{
    /// <summary>
    /// Margen mínimo exigido (minutos) entre el peor caso de cada camino y el presupuesto total — un colchón
    /// para el overhead propio del código (logs, round-trips a la base, serialización) que no está contado en
    /// los timeouts de cada paso individual.
    /// </summary>
    private const int MinimumMarginMinutes = 5;

    [Fact]
    public void MaxDuration_CubreElPeorCasoDeLosPasosANTESDelTouch_ChequeoDeEsquemaMasBackupMasCopiaMinio()
    {
        var peorCasoAntesDelRestore =
            PgDatabaseRestorePort.DefaultSchemaCheckTimeoutMinutes
            + PgDumpAndMinioWipeBackupPort.DefaultPgDumpTimeoutMinutes
            + PgDumpAndMinioWipeBackupPort.DefaultMinioCopyTimeoutMinutes;

        var maxDuration = FileMaintenanceModeService.DefaultMaxMaintenanceDurationMinutes;

        Assert.True(
            maxDuration >= peorCasoAntesDelRestore + MinimumMarginMinutes,
            $"Maintenance:MaxDurationMinutes ({maxDuration}) tiene que cubrir el peor caso de los pasos ANTES " +
            $"del pg_restore (chequeo de esquema {PgDatabaseRestorePort.DefaultSchemaCheckTimeoutMinutes} + " +
            $"backup previo {PgDumpAndMinioWipeBackupPort.DefaultPgDumpTimeoutMinutes} + copia MinIO " +
            $"{PgDumpAndMinioWipeBackupPort.DefaultMinioCopyTimeoutMinutes} = {peorCasoAntesDelRestore}) " +
            $"más un margen mínimo de {MinimumMarginMinutes} minutos.");
    }

    [Fact]
    public void MaxDuration_CubreElPeorCasoDelPgRestoreMismo_MedidoDesdeElTouch()
    {
        var peorCasoDelRestore = PgDatabaseRestorePort.DefaultPgRestoreTotalTimeoutMinutes;
        var maxDuration = FileMaintenanceModeService.DefaultMaxMaintenanceDurationMinutes;

        Assert.True(
            maxDuration >= peorCasoDelRestore + MinimumMarginMinutes,
            $"Maintenance:MaxDurationMinutes ({maxDuration}) tiene que cubrir el timeout propio del pg_restore " +
            $"({peorCasoDelRestore}) más un margen mínimo de {MinimumMarginMinutes} minutos — el reloj de " +
            "auto-expiración se resetea (Touch) justo antes de arrancarlo, así que este es el único camino " +
            "que importa DESDE ese punto en adelante.");
    }
}
