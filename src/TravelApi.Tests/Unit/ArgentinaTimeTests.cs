using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bug critico fiscal (barrido PROD 2026-07-22, hallazgo #1): una factura emitida a las
/// ~22hs de Argentina salio con "Fecha de Emisión: 23/07/2026" (un dia despues) porque el
/// backend armaba las fechas con la hora del SERVIDOR (UTC), no con la hora argentina.
///
/// Estos tests blindan <see cref="ArgentinaTime"/>, el helper unico (regla T-4) que reemplaza
/// todo <c>DateTime.Now</c>/<c>DateTime.UtcNow.AddHours(-3)</c> usado para fechas visibles de
/// comprobantes (factura/NC/ND) y documentos internos (voucher, recibo).
/// </summary>
public class ArgentinaTimeTests
{
    /// <summary>
    /// Caso del bug real: un instante UTC que en Argentina todavia es 22/07 a la noche, pero en
    /// UTC ya es 23/07 de madrugada (Argentina esta 3 horas atras de UTC). El dia calendario
    /// argentino tiene que seguir siendo 22/07, NO 23/07.
    /// </summary>
    [Fact]
    public void ToArgentinaTime_InstanteDeMadrugadaEnUtc_DevuelveElDiaAnteriorEnArgentina()
    {
        // 23/07/2026 01:30 UTC = 22/07/2026 22:30 hora argentina (UTC-3).
        var instanteUtc = new DateTime(2026, 07, 23, 1, 30, 0, DateTimeKind.Utc);

        var resultado = ArgentinaTime.ToArgentinaTime(instanteUtc);

        Assert.Equal(new DateTime(2026, 07, 22, 22, 30, 0), resultado);
        Assert.Equal(new DateTime(2026, 07, 22), resultado.Date);
    }

    /// <summary>
    /// Caso simetrico: un instante de madrugada en UTC (00:30) todavia es el dia ANTERIOR en
    /// Argentina (21:30 del dia previo). Confirma que la conversion resta 3 horas de pared
    /// siempre, sin importar en que momento del dia UTC se calcule.
    /// </summary>
    [Fact]
    public void ToArgentinaTime_InstanteDeMadrugadaUtc_QueEnUtcYaEsOtroDia_ConvierteAlDiaArgentinoCorrecto()
    {
        // 01/01/2027 00:30 UTC = 31/12/2026 21:30 hora argentina.
        var instanteUtc = new DateTime(2027, 01, 01, 0, 30, 0, DateTimeKind.Utc);

        var resultado = ArgentinaTime.ToArgentinaTime(instanteUtc);

        Assert.Equal(new DateTime(2026, 12, 31, 21, 30, 0), resultado);
        Assert.Equal(new DateTime(2026, 12, 31), resultado.Date);
    }

    /// <summary>
    /// Un instante de la tarde en UTC (sin cruce de medianoche) tiene que dar el MISMO dia
    /// calendario en Argentina, solo con la hora corrida 3 horas atras.
    /// </summary>
    [Fact]
    public void ToArgentinaTime_InstanteDeLaTardeEnUtc_MismoDiaCalendario()
    {
        var instanteUtc = new DateTime(2026, 07, 22, 15, 0, 0, DateTimeKind.Utc);

        var resultado = ArgentinaTime.ToArgentinaTime(instanteUtc);

        Assert.Equal(new DateTime(2026, 07, 22, 12, 0, 0), resultado);
    }

    /// <summary>
    /// Defensivo: si llega un DateTime con Kind=Unspecified (por ejemplo un valor leido de una
    /// columna que perdio el Kind), se trata como si fuera UTC — es la misma convencion que usa
    /// el resto del sistema para CreatedAt/IssuedAt/PaidAt. No debe explotar ni devolver un valor
    /// sin sentido.
    /// </summary>
    [Fact]
    public void ToArgentinaTime_ConKindUnspecified_LoTrataComoUtc()
    {
        var instanteSinKind = new DateTime(2026, 07, 23, 1, 30, 0, DateTimeKind.Unspecified);

        var resultado = ArgentinaTime.ToArgentinaTime(instanteSinKind);

        Assert.Equal(new DateTime(2026, 07, 22, 22, 30, 0), resultado);
    }

    /// <summary>
    /// NIT N1 (cierre defensivo, revision reviewer): un DateTime con Kind=Local no puede simplemente
    /// re-etiquetarse como UTC (seria incorrecto salvo que el huso local de la maquina sea UTC+0).
    /// Verificamos la propiedad que si tiene que cumplirse sin importar en que huso corra el test:
    /// convertir un DateTime Local da EXACTAMENTE el mismo resultado que convertir ese mismo
    /// instante ya pasado a UTC a mano (equivalencia estructural, no depende del huso de la maquina
    /// que ejecuta el test).
    /// </summary>
    [Fact]
    public void ToArgentinaTime_ConKindLocal_ConvierteAlMismoInstanteQueSuEquivalenteUtc()
    {
        var instanteLocal = new DateTime(2026, 07, 23, 1, 30, 0, DateTimeKind.Local);
        var mismoInstanteEnUtc = instanteLocal.ToUniversalTime();

        var resultadoDesdeLocal = ArgentinaTime.ToArgentinaTime(instanteLocal);
        var resultadoDesdeUtc = ArgentinaTime.ToArgentinaTime(mismoInstanteEnUtc);

        Assert.Equal(resultadoDesdeUtc, resultadoDesdeLocal);
    }

    /// <summary>
    /// GetArgentinaNow es GetArgentinaNow = ToArgentinaTime(UtcNow): verificamos que el resultado
    /// caiga siempre entre 4 y 2 horas antes que DateTime.UtcNow (el offset real es 3 horas fijas
    /// hoy; dejamos margen para no volver el test fragil ante el tiempo que tarda en ejecutar).
    /// </summary>
    [Fact]
    public void GetArgentinaNow_EstaSiempreTresHorasDetrasDeUtcNow()
    {
        var antesUtc = DateTime.UtcNow;

        var argentinaNow = ArgentinaTime.GetArgentinaNow();

        var diferenciaHoras = (antesUtc - argentinaNow).TotalHours;

        Assert.InRange(diferenciaHoras, 2.9, 3.1);
    }

    /// <summary>
    /// GetArgentinaToday tiene que devolver el Date (sin componente horario) del resultado de
    /// GetArgentinaNow — nunca DateTime.Today, que usaria el huso del servidor.
    /// </summary>
    [Fact]
    public void GetArgentinaToday_DevuelveSoloElDiaCalendarioSinHora()
    {
        var hoyArgentina = ArgentinaTime.GetArgentinaToday();

        Assert.Equal(hoyArgentina.Date, hoyArgentina);
        Assert.Equal(0, hoyArgentina.TimeOfDay.TotalSeconds);
    }
}
