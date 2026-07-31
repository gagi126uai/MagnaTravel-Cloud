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
}
