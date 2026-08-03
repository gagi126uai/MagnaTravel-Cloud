using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para>Estos tests fijan el comportamiento de los validadores PUROS (sin base de datos): mail,
/// telefono, CBU, punto de venta, porcentaje de comision y condicion fiscal. Los tests de las PUERTAS
/// (que cada alta/edicion realmente los use) viven en <c>FieldValidationEntryPointsTests</c>, igual que
/// la separacion que ya existe entre <c>CuitValidator</c> y <c>CuitValidationEntryPointsTests</c>.</para>
///
/// <para>Regla comun a todos: <b>vacio es VALIDO</b>. Estos gates frenan un dato MAL CARGADO, no exigen
/// que el dato exista (hay clientes sin mail, operadores del exterior sin CUIT, cuentas con alias y sin
/// CBU).</para>
/// </summary>
public class FieldValidatorsTests
{
    // ===================================================================================================
    // Mail
    // ===================================================================================================

    [Theory]
    [InlineData("juan@gmail.com")]
    [InlineData("juan.perez@magnaviajesyturismo.com.ar")]
    [InlineData("ventas+reservas@agencia.com")]
    [InlineData("  juan@gmail.com  ")] // espacios al pegar desde WhatsApp/Excel: se recortan
    public void EmailValidator_MailesBienEscritos_SonValidos(string email)
    {
        Assert.True(EmailValidator.IsValidOrEmpty(email));
    }

    [Theory]
    [InlineData("juan")]              // sin arroba
    [InlineData("juan@")]             // sin dominio
    [InlineData("@gmail.com")]        // sin nombre
    [InlineData("juan@gmail")]        // sin punto: es el error de tipeo mas comun
    [InlineData("juan @gmail.com")]   // espacio en el medio
    [InlineData("juan@gmail..com")]   // dos puntos seguidos: queda un pedazo vacio
    [InlineData("juan@@gmail.com")]   // dos arrobas
    [InlineData("no tiene")]          // texto libre, el caso real que se colaba antes
    public void EmailValidator_MailesMalEscritos_SonInvalidos(string email)
    {
        Assert.False(EmailValidator.IsValidOrEmpty(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmailValidator_Vacio_EsValido(string? email)
    {
        Assert.True(EmailValidator.IsValidOrEmpty(email));
    }

    // ===================================================================================================
    // Telefono
    // ===================================================================================================

    [Theory]
    [InlineData("3511234567")]           // celular sin formato
    [InlineData("+54 9 351 123-4567")]   // formato internacional completo
    [InlineData("(0351) 422-1234")]      // formato de fijo con caracteristica
    [InlineData("351123")]               // 6 digitos: el minimo aceptado
    [InlineData("+123456789012345")]     // 15 digitos: el maximo del estandar internacional
    public void PhoneValidator_TelefonosRazonables_SonValidos(string phone)
    {
        Assert.True(PhoneValidator.IsValidOrEmpty(phone));
    }

    [Theory]
    [InlineData("no tiene")]                        // texto libre
    [InlineData("preguntar a la hermana")]          // el caso real que se colaba antes
    [InlineData("3511234567 int. 45")]              // letras mezcladas con el numero
    [InlineData("12345")]                           // 5 digitos: muy corto para ser un telefono
    [InlineData("+1234567890123456")]               // 16 digitos: pasa el maximo internacional
    [InlineData("11+22")]                           // el "+" solo vale adelante, no en el medio
    [InlineData("351-1234567@")]                    // simbolo que no es separador de formato
    public void PhoneValidator_LoQueNoEsUnTelefono_EsInvalido(string phone)
    {
        Assert.False(PhoneValidator.IsValidOrEmpty(phone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PhoneValidator_Vacio_EsValido(string? phone)
    {
        Assert.True(PhoneValidator.IsValidOrEmpty(phone));
    }

    // ===================================================================================================
    // CBU
    // ===================================================================================================

    [Theory]
    // CBU real del circuito bancario argentino: los dos digitos verificadores cierran.
    [InlineData("2850590940090418135201")]
    // Armado con el algoritmo del BCRA (banco 011 = Nacion, sucursal 0599) y verificado a mano en el
    // docstring de CbuValidator.
    [InlineData("0110599520000001234569")]
    // El mismo CBU pegado del homebanking, que suele venir con espacios cada 4 digitos.
    [InlineData("2850 5909 4009 0418 1352 01")]
    public void CbuValidator_CbuConVerificadoresCorrectos_EsValido(string cbu)
    {
        Assert.True(CbuValidator.IsValidOrEmpty(cbu));
    }

    [Theory]
    // Mismo CBU valido de arriba con el ULTIMO digito cambiado: falla el verificador del bloque 2.
    [InlineData("0110599520000001234568")]
    // Mismo CBU con el 8vo digito cambiado: falla el verificador del bloque 1.
    [InlineData("0110599420000001234569")]
    // 22 digitos "lindos" pero inventados: es exactamente lo que el chequeo viejo (solo largo) dejaba pasar.
    [InlineData("0123456789012345678901")]
    [InlineData("1234567890123456789012")]
    // Largos que no son 22.
    [InlineData("28505909400904181352")]
    [InlineData("285059094009041813520100")]
    // Letras adentro.
    [InlineData("28505909400904181352AB")]
    public void CbuValidator_CbuQueNoCierra_EsInvalido(string cbu)
    {
        Assert.False(CbuValidator.IsValidOrEmpty(cbu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CbuValidator_Vacio_EsValido(string? cbu)
    {
        // Una cuenta puede cargarse solo con alias: el CBU es opcional.
        Assert.True(CbuValidator.IsValidOrEmpty(cbu));
    }

    // ===================================================================================================
    // Punto de venta de ARCA
    // ===================================================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(99998)]
    public void AfipPointOfSaleValidator_DentroDelRango_EsValido(int pointOfSale)
    {
        Assert.True(AfipPointOfSaleValidator.IsValid(pointOfSale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(99999)]
    [InlineData(100000)]
    public void AfipPointOfSaleValidator_FueraDelRango_EsInvalido(int pointOfSale)
    {
        Assert.False(AfipPointOfSaleValidator.IsValid(pointOfSale));
    }

    // ===================================================================================================
    // Porcentaje de comision
    // ===================================================================================================

    [Theory]
    [InlineData(0)]     // "sin comision" es una configuracion real
    [InlineData(10)]
    [InlineData(100)]
    public void CommissionPercentValidator_EntreCeroYCien_EsValido(decimal percent)
    {
        Assert.True(CommissionPercentValidator.IsValid(percent));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.5)]
    [InlineData(1000)]
    public void CommissionPercentValidator_FueraDeRango_EsInvalido(decimal percent)
    {
        Assert.False(CommissionPercentValidator.IsValid(percent));
    }

    // ===================================================================================================
    // Condicion fiscal
    // ===================================================================================================

    [Theory]
    [InlineData("Responsable Inscripto")]   // como lo manda la pantalla de la agencia
    [InlineData("Monotributo")]
    [InlineData("Exento")]
    [InlineData("Consumidor Final")]
    [InlineData("IVA_RESP_INSCRIPTO")]      // como lo manda la pantalla de operadores
    [InlineData("MONOTRIBUTISTA")]
    [InlineData("IVA_EXENTO")]
    [InlineData("CONSUMIDOR_FINAL")]
    public void TaxConditionValidator_OpcionesRealesDeLasPantallas_SonValidas(string taxCondition)
    {
        Assert.True(TaxConditionValidator.IsKnownTextOrEmpty(taxCondition));
    }

    [Theory]
    [InlineData("Responsable")]
    [InlineData("RI")]
    [InlineData("cualquier cosa")]
    [InlineData("Monotributo Social")]
    public void TaxConditionValidator_TextoQueElMotorNoReconoce_EsInvalido(string taxCondition)
    {
        Assert.False(TaxConditionValidator.IsKnownTextOrEmpty(taxCondition));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TaxConditionValidator_TextoVacio_EsValido(string? taxCondition)
    {
        Assert.True(TaxConditionValidator.IsKnownTextOrEmpty(taxCondition));
    }

    [Theory]
    [InlineData(1)] // Responsable Inscripto
    [InlineData(4)] // Exento
    [InlineData(5)] // Consumidor Final
    [InlineData(6)] // Monotributo
    public void TaxConditionValidator_CodigosDelDesplegableDelCliente_SonValidos(int taxConditionId)
    {
        Assert.True(TaxConditionValidator.IsKnownCustomerCodeOrEmpty(taxConditionId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void TaxConditionValidator_CodigoFueraDelCatalogo_EsInvalido(int taxConditionId)
    {
        Assert.False(TaxConditionValidator.IsKnownCustomerCodeOrEmpty(taxConditionId));
    }

    [Fact]
    public void TaxConditionValidator_CodigoAusente_EsValido()
    {
        // null = "el request no mando el campo": no se toca la condicion guardada.
        Assert.True(TaxConditionValidator.IsKnownCustomerCodeOrEmpty(null));
    }

    // ===================================================================================================
    // TANDA 2 — Numero de documento (segun el TIPO elegido)
    // ===================================================================================================

    [Theory]
    [InlineData("12345678")]   // DNI de 8 numeros, el caso normal de hoy
    [InlineData("9876543")]    // DNI viejo de 7 numeros
    [InlineData(" 12345678 ")] // espacios al pegar desde Excel: se recortan
    public void DocumentNumberValidator_DniBienCargado_EsValido(string documentNumber)
    {
        Assert.True(DocumentNumberValidator.IsValidOrEmpty("DNI", documentNumber));
    }

    [Theory]
    [InlineData("12.345.678")]   // con puntos: el sistema guarda el numero limpio
    [InlineData("123456")]       // 6 numeros: muy corto
    [InlineData("123456789")]    // 9 numeros: muy largo
    [InlineData("20345678901")]  // pegaron el CUIT en el casillero del DNI
    [InlineData("AB123456")]     // numero de pasaporte con el tipo DNI elegido
    [InlineData("no lo trajo")]  // texto libre, el caso real que se colaba antes
    public void DocumentNumberValidator_DniMalCargado_EsInvalido(string documentNumber)
    {
        Assert.False(DocumentNumberValidator.IsValidOrEmpty("DNI", documentNumber));
    }

    [Theory]
    [InlineData("dni")]
    [InlineData("D.N.I.")]
    public void DocumentNumberValidator_ElTipoDniSeReconoceEscritoDeCualquierForma(string documentType)
    {
        // Mismo numero invalido: si el tipo se reconoce como DNI, tiene que rebotar igual.
        Assert.True(DocumentNumberValidator.IsDniType(documentType));
        Assert.False(DocumentNumberValidator.IsValidOrEmpty(documentType, "12.345.678"));
    }

    [Theory]
    [InlineData("Pasaporte", "AB123456")]
    [InlineData("Pasaporte", "AAB-123456")]
    [InlineData("Cedula", "1234567")]
    [InlineData("Otro", "X 99/88")]
    [InlineData("Pasaporte", "20345678901")] // un pasaporte todo numerico existe en varios paises
    public void DocumentNumberValidator_DocumentosDeTextoLibre_SonValidos(string documentType, string documentNumber)
    {
        Assert.True(DocumentNumberValidator.IsValidOrEmpty(documentType, documentNumber));
    }

    [Theory]
    [InlineData("Pasaporte", "???")]                                  // puro simbolo: no es un documento
    [InlineData("Otro", "@@@")]
    [InlineData("Pasaporte", "el pasaporte lo manda por mail mañana")] // una frase entera en el casillero
    public void DocumentNumberValidator_LoQueNoPuedeSerUnDocumento_EsInvalido(string documentType, string documentNumber)
    {
        Assert.False(DocumentNumberValidator.IsValidOrEmpty(documentType, documentNumber));
    }

    [Theory]
    [InlineData("DNI", null)]
    [InlineData("DNI", "")]
    [InlineData("Pasaporte", "   ")]
    [InlineData(null, null)]
    public void DocumentNumberValidator_Vacio_EsValido(string? documentType, string? documentNumber)
    {
        Assert.True(DocumentNumberValidator.IsValidOrEmpty(documentType, documentNumber));
    }

    [Fact]
    public void DocumentNumberValidator_ElMensajeCambiaSegunElTipo()
    {
        // El del DNI explica el formato exacto; el de los demas es generico porque no hay uno solo.
        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, DocumentNumberValidator.MessageFor("DNI"));
        Assert.Equal(DocumentNumberValidator.InvalidDocumentNumberMessage, DocumentNumberValidator.MessageFor("Pasaporte"));
    }

    // ===================================================================================================
    // TANDA 2 — Fecha de nacimiento
    // ===================================================================================================

    private static readonly DateTime HoyDePrueba = new(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-30)]     // hace 30 dias
    [InlineData(-40 * 365)] // una persona de 40 anos
    [InlineData(0)]       // hoy mismo: un bebe recien nacido tambien viaja
    public void BirthDateValidator_FechasPosibles_SonValidas(int daysFromToday)
    {
        Assert.True(BirthDateValidator.IsValidOrEmpty(HoyDePrueba.AddDays(daysFromToday), HoyDePrueba));
    }

    [Fact]
    public void BirthDateValidator_FechaFutura_EsInvalida()
    {
        // El error de tipeo tipico: el ano de HOY o uno posterior en lugar del de nacimiento.
        Assert.False(BirthDateValidator.IsValidOrEmpty(HoyDePrueba.AddDays(1), HoyDePrueba));
    }

    [Fact]
    public void BirthDateValidator_MasDeCientoVeinteAnos_EsInvalida()
    {
        var demasiadoVieja = HoyDePrueba.AddYears(-120).AddDays(-1);
        Assert.False(BirthDateValidator.IsValidOrEmpty(demasiadoVieja, HoyDePrueba));
    }

    [Fact]
    public void BirthDateValidator_JustoCientoVeinteAnos_EsValida()
    {
        // El borde entra: 120 anos exactos todavia es una persona posible.
        Assert.True(BirthDateValidator.IsValidOrEmpty(HoyDePrueba.AddYears(-120), HoyDePrueba));
    }

    [Fact]
    public void BirthDateValidator_SinFecha_EsValida()
    {
        Assert.True(BirthDateValidator.IsValidOrEmpty(null, HoyDePrueba));
    }

    // ===================================================================================================
    // D2 (2026-07-31 tarde) — Semaforo de pasaporte CONTRA LAS FECHAS DEL VIAJE (GetAlertOrNull)
    //
    // Arreglo 4 (retomo 2026-08-03): la tanda 2 original tambien probaba GetExpiredWarningOrNull, un
    // metodo "vencido a secas, sin mirar fechas de viaje" que quedo SIN caller productivo desde que
    // GetAlertOrNull (de abajo) lo reemplazo en todos los usos reales — se confirmo por grep antes de
    // borrarlo, junto con estos tests. GetAlertOrNull YA cubre el mismo caso "sin fechas de viaje cargadas"
    // (ver el primer test de este bloque), asi que no se perdio cobertura.
    // ===================================================================================================

    [Fact]
    public void PassportAlert_SinVencimientoCargado_NoAvisaAunqueHayaFechasDeViaje()
    {
        var viajeFin = HoyDePrueba.AddDays(10);
        Assert.Null(PassportExpiryRules.GetAlertOrNull(null, HoyDePrueba, viajeFin, HoyDePrueba));
    }

    [Fact]
    public void PassportAlert_SinFechasDeViaje_VencidoHoy_UsaLaReglaVieja()
    {
        var vencidoAyer = HoyDePrueba.AddDays(-1);
        var alerta = PassportExpiryRules.GetAlertOrNull(vencidoAyer, tripStart: null, tripEnd: null, HoyDePrueba);

        Assert.NotNull(alerta);
        Assert.Equal(PassportAlertLevel.Expired, alerta!.Level);
        // T-6: literal EXACTO, no la constante (comparar contra la propia constante es una tautologia
        // que nunca falla aunque el texto cambie sin darse cuenta).
        Assert.Equal("El pasaporte de este pasajero está vencido.", alerta.Text);
    }

    [Fact]
    public void PassportAlert_SinFechasDeViaje_Vigente_NoAvisa()
    {
        Assert.Null(PassportExpiryRules.GetAlertOrNull(HoyDePrueba.AddYears(2), tripStart: null, tripEnd: null, HoyDePrueba));
    }

    [Fact]
    public void PassportAlert_VenceAntesDelFinDelViaje_EsRojoConElTextoDeViaje()
    {
        var finDeViaje = HoyDePrueba.AddDays(30);
        var vencimiento = HoyDePrueba.AddDays(15); // vence ANTES de terminar el viaje

        var alerta = PassportExpiryRules.GetAlertOrNull(vencimiento, HoyDePrueba, finDeViaje, HoyDePrueba);

        Assert.NotNull(alerta);
        Assert.Equal(PassportAlertLevel.Expired, alerta!.Level);
        // T-6: literal EXACTO, no la constante.
        Assert.Equal("El pasaporte de este pasajero se vence antes del fin del viaje.", alerta.Text);
    }

    [Fact]
    public void PassportAlert_VenceJustoElUltimoDiaDelViaje_EsRojo()
    {
        // "vencimiento <= fin del viaje" (borde incluido): el mismo dia que termina el viaje no alcanza
        // para volver a entrar al pais con el pasaporte vigente.
        var finDeViaje = HoyDePrueba.AddDays(30);

        var alerta = PassportExpiryRules.GetAlertOrNull(finDeViaje, HoyDePrueba, finDeViaje, HoyDePrueba);

        Assert.NotNull(alerta);
        Assert.Equal(PassportAlertLevel.Expired, alerta!.Level);
    }

    [Fact]
    public void PassportAlert_VenceConMenosDeSeisMesesDeMargenDespuesDelViaje_EsAmbar()
    {
        var finDeViaje = HoyDePrueba.AddDays(30);
        // Vence 3 meses despues de terminar el viaje: alcanza para viajar, pero no le sobran los 6 meses
        // de margen que piden muchos destinos.
        var vencimiento = finDeViaje.AddMonths(3);

        var alerta = PassportExpiryRules.GetAlertOrNull(vencimiento, HoyDePrueba, finDeViaje, HoyDePrueba);

        Assert.NotNull(alerta);
        Assert.Equal(PassportAlertLevel.Tight, alerta!.Level);
        // T-6: literal EXACTO, no la constante.
        Assert.Equal(
            "Al pasaporte le quedan menos de 6 meses después del viaje; muchos destinos exigen ese margen.",
            alerta.Text);
    }

    [Fact]
    public void PassportAlert_VenceConSeisMesesOMasDeMargenDespuesDelViaje_NoAvisaNada()
    {
        var finDeViaje = HoyDePrueba.AddDays(30);
        var vencimientoHolgado = finDeViaje.AddMonths(6); // borde: exactamente 6 meses no es "menos de 6"

        Assert.Null(PassportExpiryRules.GetAlertOrNull(vencimientoHolgado, HoyDePrueba, finDeViaje, HoyDePrueba));
    }

    [Fact]
    public void PassportAlert_SinFechaDeFin_UsaLaFechaDeInicioComoReemplazo()
    {
        // La reserva solo tiene fecha de INICIO cargada (no de fin): la regla dice "si no hay fin, usar
        // inicio". Un vencimiento antes del inicio del viaje tiene que dar ROJO igual.
        var inicioDeViaje = HoyDePrueba.AddDays(20);
        var vencimiento = HoyDePrueba.AddDays(10);

        var alerta = PassportExpiryRules.GetAlertOrNull(vencimiento, inicioDeViaje, tripEnd: null, HoyDePrueba);

        Assert.NotNull(alerta);
        Assert.Equal(PassportAlertLevel.Expired, alerta!.Level);
    }
}
