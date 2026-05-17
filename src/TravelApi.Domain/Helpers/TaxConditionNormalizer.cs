using System.Globalization;
using System.Text;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// FC1.2.0 v3 §2.5 (2026-05-17): enum canonico de condiciones fiscales.
///
/// **Por que existe**: el repo tiene 3+ "shapes" para representar la misma
/// condicion fiscal:
///   - <c>Supplier.TaxCondition</c> usa <c>SHOUTY_SNAKE_CASE</c>
///     ("MONOTRIBUTISTA", "IVA_RESP_INSCRIPTO", "IVA_EXENTO") — ver
///     <see cref="TravelApi.Domain.Entities.TaxConditions"/>.
///   - <c>Customer.TaxCondition</c> y <c>AgencySettings.TaxCondition</c>
///     usan texto libre ("Monotributo", "Responsable Inscripto",
///     "Consumidor Final", "Exento") — formato visible al usuario.
///   - El frontend ofrece dropdowns con valores fijos pero historicamente
///     hay registros con variaciones de mayusculas/espacios.
///
/// El <see cref="FiscalSnapshot"/> del modulo FC1.2 va a persistir SIEMPRE
/// el formato canonico (<see cref="ToStorageString"/>) para que:
///   - Los CHECK SQL futuros tengan valores deterministicos.
///   - Los tests no dependan del formato exacto de la fuente.
///   - El reporte fiscal use UN solo formato.
///
/// **NO** reemplaza los strings legacy en las entidades de origen — esos
/// se mantienen como estan. Este enum es exclusivo del modulo de cancelacion.
/// </summary>
public enum TaxConditionCanonical
{
    /// <summary>Valor por defecto si el origen es null, vacio o desconocido. NO valido para persistir un snapshot fiscal de T0.</summary>
    Unknown = 0,

    /// <summary>Monotributo (Pequeno Contribuyente). NO genera credito fiscal IVA. AFIP code = 6.</summary>
    Monotributista = 1,

    /// <summary>Responsable Inscripto en IVA. Genera credito fiscal. AFIP code = 1.</summary>
    ResponsableInscripto = 2,

    /// <summary>IVA Exento (sin obligacion de inscripcion en IVA). AFIP code = 4.</summary>
    Exento = 3,

    /// <summary>Consumidor Final (sin CUIT o no inscripto). AFIP code = 5.</summary>
    ConsumidorFinal = 4,

    /// <summary>Extranjero (no residente fiscal en AR). Default para casos sin codigo AFIP local.</summary>
    Extranjero = 5,
}

/// <summary>
/// FC1.2.0 v3 §2.5 (MR-V2-01, 2026-05-17): conversor bidireccional entre los
/// strings legacy del repo y <see cref="TaxConditionCanonical"/>.
///
/// **Donde se usa**: <c>BookingCancellationService.ConfirmAsync</c> al armar
/// el <see cref="FiscalSnapshot"/> de T0. El service llama
/// <see cref="Normalize"/> sobre <c>Customer.TaxCondition</c>,
/// <c>Supplier.TaxCondition</c> y <c>AgencySettings.TaxCondition</c>; despues
/// persiste <see cref="ToStorageString"/> en
/// <c>FiscalSnapshot.CustomerTaxConditionAtEvent</c> etc.
///
/// **Que pasa con valores desconocidos**: devuelven <see cref="TaxConditionCanonical.Unknown"/>.
/// El caller decide si rechazar la operacion (preferido para T0) o degradar.
/// El normalizer NO loggea (es static sin dependencias) — el caller es quien
/// tiene contexto del flujo y puede emitir el log con _logger.
/// </summary>
public static class TaxConditionNormalizer
{
    /// <summary>
    /// Mapea un string del repo a su canonico. Case-insensitive, trimea espacios
    /// y **maneja variantes con/sin tildes** (ej. "Monotríbutista" -> Monotributista).
    /// Devuelve <see cref="TaxConditionCanonical.Unknown"/> ante null, vacio,
    /// whitespace, o un valor que no este en la tabla.
    ///
    /// Variantes cubiertas (verificadas con grep contra el repo el 2026-05-17):
    ///
    /// | Origen                                                                       | Canonical            |
    /// |------------------------------------------------------------------------------|----------------------|
    /// | "MONOTRIBUTISTA"      (Supplier.cs <c>TaxConditions.Monotributista</c>)      | Monotributista       |
    /// | "Monotributo"         (AgencySettings, Customer, FiscalController, frontend)| Monotributista       |
    /// | "IVA_RESP_INSCRIPTO"  (Supplier.cs <c>TaxConditions.IvaResponsableInscripto</c>) | ResponsableInscripto |
    /// | "Responsable Inscripto" (AgencySettings, Customer, FiscalController)         | ResponsableInscripto |
    /// | "IVA_EXENTO"          (Supplier.cs <c>TaxConditions.IvaExento</c>)           | Exento               |
    /// | "Exento"              (texto libre)                                          | Exento               |
    /// | "CONSUMIDOR_FINAL"    (Supplier.cs <c>TaxConditions.ConsumidorFinal</c>)     | ConsumidorFinal      |
    /// | "Consumidor Final"    (Customer default, frontend)                           | ConsumidorFinal      |
    /// | "Monotríbutista" / "Éxento" / "Responsablé Inscripto" (typos con tilde)      | mismo canonico       |
    /// | null / "" / whitespace                                                       | Unknown              |
    /// | cualquier otro                                                               | Unknown              |
    ///
    /// **Mantenimiento operativo**: si encontras un string del repo que mapea a
    /// <c>Unknown</c> inesperadamente, agrega el literal al switch y un test al
    /// <c>TaxConditionNormalizerTests</c>. Antes de prender el feature flag de
    /// cancelacion/refund en produccion, correr contra la base real:
    /// <code>
    /// SELECT DISTINCT "TaxCondition" FROM "Customers" WHERE "TaxCondition" IS NOT NULL;
    /// SELECT DISTINCT "TaxCondition" FROM "Suppliers" WHERE "TaxCondition" IS NOT NULL;
    /// SELECT DISTINCT "TaxCondition" FROM "AgencySettings" WHERE "TaxCondition" IS NOT NULL;
    /// </code>
    /// para confirmar que esta tabla cubre todas las variantes en uso. Si aparece
    /// algo que no esta, no prender el flag hasta agregar el mapeo + test.
    /// </summary>
    public static TaxConditionCanonical Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TaxConditionCanonical.Unknown;
        }

        // Normalizo a una clave deterministica:
        //   1) trim para evitar que espacios accidentales hagan miss.
        //   2) RemoveDiacritics para que "Monotríbutista" -> "Monotributista".
        //      Pasa porque algunos sistemas legacy de carga manual escribieron
        //      el campo con tildes (vendedor copia-pega de un Word). Sin esto
        //      caerian en Unknown y T0 fallaria innecesariamente.
        //   3) ToUpperInvariant para case-insensitive.
        // El orden importa: RemoveDiacritics sobre upper o lower funciona igual,
        // pero hacerlo antes del Upper deja el codigo simetrico con como uno
        // suele documentar la normalizacion en libros de texto (NFD -> filtro).
        var key = RemoveDiacritics(raw.Trim()).ToUpperInvariant();

        // Monotributo / Monotributista.
        if (key == "MONOTRIBUTISTA" ||
            key == "MONOTRIBUTO")
        {
            return TaxConditionCanonical.Monotributista;
        }

        // Responsable Inscripto / IVA_RESP_INSCRIPTO / RESPONSABLE_INSCRIPTO (canonical storage).
        if (key == "RESPONSABLE INSCRIPTO" ||
            key == "RESPONSABLE_INSCRIPTO" ||
            key == "IVA_RESP_INSCRIPTO" ||
            key == "IVA RESP INSCRIPTO")
        {
            return TaxConditionCanonical.ResponsableInscripto;
        }

        // Exento / IVA_EXENTO.
        if (key == "EXENTO" ||
            key == "IVA_EXENTO" ||
            key == "IVA EXENTO")
        {
            return TaxConditionCanonical.Exento;
        }

        // Consumidor Final / CONSUMIDOR_FINAL (canonical storage).
        if (key == "CONSUMIDOR FINAL" ||
            key == "CONSUMIDOR_FINAL")
        {
            return TaxConditionCanonical.ConsumidorFinal;
        }

        // Extranjero (no esta en el repo todavia, lo dejamos preparado para FC4).
        if (key == "EXTRANJERO" ||
            key == "FOREIGN" ||
            key == "FOREIGNER")
        {
            return TaxConditionCanonical.Extranjero;
        }

        return TaxConditionCanonical.Unknown;
    }

    /// <summary>
    /// Devuelve el formato canonico que persistimos en
    /// <see cref="FiscalSnapshot"/>: SHOUTY_SNAKE_CASE del enum.
    ///
    /// **Por que SHOUTY_SNAKE_CASE y no el nombre del enum**: alineado al
    /// formato que ya usa <see cref="TravelApi.Domain.Entities.TaxConditions"/>
    /// para los suppliers (MONOTRIBUTISTA, IVA_RESP_INSCRIPTO, ...). Asi un
    /// CHECK SQL futuro sobre <c>FiscalSnapshot.SupplierTaxConditionAtEvent</c>
    /// puede compararse contra esos mismos literales.
    ///
    /// Mapeo (la tabla es la fuente de verdad para tests):
    ///   - Monotributista       -> "MONOTRIBUTISTA"
    ///   - ResponsableInscripto -> "RESPONSABLE_INSCRIPTO"
    ///   - Exento               -> "EXENTO"
    ///   - ConsumidorFinal      -> "CONSUMIDOR_FINAL"
    ///   - Extranjero           -> "EXTRANJERO"
    ///   - Unknown              -> "UNKNOWN"
    /// </summary>
    public static string ToStorageString(TaxConditionCanonical canonical)
    {
        // Switch explicito en vez de <c>canonical.ToString().ToUpperInvariant()</c>
        // porque queremos garantizar el formato exacto independiente del nombre
        // del enum (si alguien renombra el enum value, este metodo no cambia y
        // los tests de round-trip lo detectan).
        return canonical switch
        {
            TaxConditionCanonical.Monotributista => "MONOTRIBUTISTA",
            TaxConditionCanonical.ResponsableInscripto => "RESPONSABLE_INSCRIPTO",
            TaxConditionCanonical.Exento => "EXENTO",
            TaxConditionCanonical.ConsumidorFinal => "CONSUMIDOR_FINAL",
            TaxConditionCanonical.Extranjero => "EXTRANJERO",
            TaxConditionCanonical.Unknown => "UNKNOWN",
            _ => "UNKNOWN",
        };
    }

    /// <summary>
    /// Saca tildes/acentos de un string preservando las letras base.
    /// Ej. "Monotríbutista" -> "Monotributista", "Éxento" -> "Exento".
    ///
    /// **Como funciona** (didactico):
    ///   - Unicode tiene 2 formas de codificar "í": como un caracter unico
    ///     (precompuesto, NFC) o como "i" + un combining accent (descompuesto, NFD).
    ///   - <see cref="string.Normalize(NormalizationForm)"/> con <c>FormD</c>
    ///     pasa todo a descompuesto. Asi "í" se transforma en "i" + "´".
    ///   - Filtramos los <see cref="UnicodeCategory.NonSpacingMark"/> (los acentos
    ///     que se dibujan encima de otra letra). Quedan solo las letras base.
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        // Volvemos a FormC (precompuesto) para conservar invariantes externas:
        // si en el futuro algun caller compara el resultado con un literal
        // pre-armado, ese literal va a estar en NFC por default. Sin esto,
        // dos strings "iguales a la vista" pueden no ser .Equals().
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
