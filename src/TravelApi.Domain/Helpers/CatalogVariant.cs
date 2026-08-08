namespace TravelApi.Domain.Helpers;

/// <summary>
/// La VARIANTE de un producto del tarifario: eso que hace que dos precios del mismo producto y el mismo
/// operador sean precios DISTINTOS y no uno que pisa al otro (spec firmada 2026-08-07, M-12).
///
/// <para><b>Por que existe</b>: hasta ahora la memoria de precios era por (producto, operador). Vender una
/// habitacion TRIPLE del mismo hotel al mismo operador PISABA el precio de la doble, y la proxima venta
/// sugeria un precio equivocado. Ahora cada combinacion recuerda lo suyo:</para>
/// <list type="bullet">
///   <item><b>Hotel</b>: habitacion + regimen + nombre fino ("Doble Superior con desayuno").</item>
///   <item><b>Aereo</b>: cabina ("Económica").</item>
///   <item><b>Traslado</b>: vehiculo ("Van").</item>
///   <item><b>Paquete y asistencia</b>: SIN variante (V2). Su clave queda vacia y se comportan igual que antes.</item>
/// </list>
///
/// <para><b>Dos datos, dos usos</b>: la <c>Key</c> es para COMPARAR (normalizada, nunca se muestra) y la
/// <c>Label</c> es para MOSTRAR (criolla, la arma el motor — T-13, el front no concatena textos).</para>
/// </summary>
public static class CatalogVariant
{
    /// <summary>Variante vacia: la de los tipos que no tienen (paquete, asistencia) o la de un dato sin cargar.</summary>
    public static readonly (string Key, string Label) None = (string.Empty, string.Empty);

    /// <summary>
    /// Arma la clave y la etiqueta de la variante de un HOTEL.
    /// Ejemplo: ("Doble", "Desayuno", "Superior") -> clave "doble|desayuno|superior",
    /// etiqueta "Doble Superior con desayuno".
    /// </summary>
    public static (string Key, string Label) ForHotel(string? roomType, string? mealPlan, string? fineName)
    {
        var room = NormalizeRoomType(roomType);
        var board = NormalizeMealPlan(mealPlan);
        var fine = TextNormalizer.NormalizeForCatalog(fineName);

        if (room.Length == 0 && board.Length == 0 && fine.Length == 0) return None;

        var key = $"{room}|{board}|{fine}";
        return (key, BuildHotelLabel(room, board, fineName));
    }

    /// <summary>Variante de un AEREO: la cabina. ("Economy" -> "Económica").</summary>
    public static (string Key, string Label) ForFlight(string? cabinClass)
    {
        var key = NormalizeCabin(cabinClass);
        if (key.Length == 0) return None;
        return (key, CabinLabel(key));
    }

    /// <summary>Variante de un TRASLADO: el vehiculo. Es texto libre del vendedor, se respeta como lo escribio.</summary>
    public static (string Key, string Label) ForTransfer(string? vehicleType)
    {
        var key = TextNormalizer.NormalizeForCatalog(vehicleType);
        if (key.Length == 0) return None;
        return (key, CapitalizeWords(vehicleType!.Trim()));
    }

    /// <summary>
    /// Resuelve la variante mirando SOLO el tipo de servicio: cada tipo sabe cual es su variante natural.
    /// Los tipos sin variante (paquete, asistencia) devuelven la vacia aunque les pasen datos.
    /// </summary>
    public static (string Key, string Label) For(
        string? serviceType,
        string? roomType = null,
        string? mealPlan = null,
        string? fineName = null,
        string? cabinClass = null,
        string? vehicleType = null)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => ForHotel(roomType, mealPlan, fineName),
            "aereo" => ForFlight(cabinClass),
            "traslado" => ForTransfer(vehicleType),
            _ => None
        };

    /// <summary>True si ese tipo de servicio tiene variante (hotel, aereo, traslado).</summary>
    public static bool AppliesTo(string? serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) is "hotel" or "aereo" or "traslado";

    // ============================================================
    // El camino de VUELTA: de la clave a las piezas que muestra el formulario
    // ============================================================

    /// <summary>
    /// Las PIEZAS sueltas de una variante, escritas como las espera el formulario de la pantalla
    /// ("Doble", "Media Pension", "Superior"). Null en la pieza que esa variante no tiene.
    /// </summary>
    public readonly record struct Parts(
        string? RoomType, string? MealPlan, string? FineName, string? CabinClass, string? VehicleType);

    /// <summary>
    /// Deshace la clave y devuelve sus piezas, para que el formulario de "Corregir" arranque con la
    /// habitación REAL en vez de con los valores por defecto ("Doble / Desayuno").
    ///
    /// <para><b>Por qué hace falta</b>: la clave está normalizada para comparar
    /// (<c>"doble|media_pension|superior"</c>) y ninguna pantalla debería tener que interpretarla — eso
    /// es jerga interna. El motor la traduce a las mismas palabras que usan los desplegables (T-13).</para>
    /// </summary>
    public static Parts PartsOf(string? serviceType, string? variantKey)
    {
        var key = variantKey ?? string.Empty;
        if (key.Length == 0) return default;

        switch (TextNormalizer.NormalizeForMatch(serviceType))
        {
            case "hotel":
                var pieces = key.Split('|');
                if (pieces.Length != 3) return default;
                return new Parts(
                    RoomType: RoomTypeValue(pieces[0]),
                    MealPlan: MealPlanValue(pieces[1]),
                    FineName: pieces[2].Length > 0 ? CapitalizeWords(pieces[2]) : null,
                    CabinClass: null,
                    VehicleType: null);

            case "aereo":
                return new Parts(null, null, null, CabinClass: CabinValue(key), VehicleType: null);

            case "traslado":
                return new Parts(null, null, null, null, VehicleType: CapitalizeWords(key));

            default:
                return default;
        }
    }

    /// <summary>"simple" -&gt; "Single": la palabra tal como la ofrece el desplegable de habitación.</summary>
    private static string? RoomTypeValue(string roomKey) => roomKey switch
    {
        "simple" => "Single",
        "doble" => "Doble",
        "twin" => "Twin",
        "triple" => "Triple",
        "cuadruple" => "Cuadruple",
        "familiar" => "Familiar",
        "suite" => "Suite",
        "" => null,
        _ => CapitalizeWords(roomKey)
    };

    /// <summary>"media_pension" -&gt; "Media Pension": la opción tal como la ofrece el desplegable de régimen.</summary>
    private static string? MealPlanValue(string boardKey) => boardKey switch
    {
        "solo_alojamiento" => "Solo Alojamiento",
        "desayuno" => "Desayuno",
        "media_pension" => "Media Pension",
        "pension_completa" => "Pension Completa",
        "todo_incluido" => "All Inclusive",
        "" => null,
        _ => CapitalizeWords(boardKey.Replace('_', ' '))
    };

    /// <summary>"ejecutiva" -&gt; "Business": la opción tal como la ofrece el desplegable de cabina.</summary>
    private static string? CabinValue(string cabinKey) => cabinKey switch
    {
        "economica" => "Economy",
        "economica_premium" => "Premium",
        "ejecutiva" => "Business",
        "primera" => "First",
        "" => null,
        _ => CapitalizeWords(cabinKey.Replace('_', ' '))
    };

    /// <summary>
    /// Cuanto se parecen dos variantes de HOTEL, de 0 a 3. Sirve para elegir "la habitacion mas parecida"
    /// cuando de la que se esta vendiendo todavia no hay precio (M-15): comparten regimen, comparten
    /// capacidad, comparten nombre fino. NO se usa para unir nada: solo para sugerir con aviso.
    /// </summary>
    public static int HotelSimilarity(string keyA, string keyB)
    {
        var a = keyA.Split('|');
        var b = keyB.Split('|');
        if (a.Length != 3 || b.Length != 3) return 0;

        var score = 0;
        if (a[0].Length > 0 && a[0] == b[0]) score += 2; // misma capacidad (doble/triple) pesa mas
        if (a[1].Length > 0 && a[1] == b[1]) score += 1; // mismo regimen
        if (a[2].Length > 0 && a[2] == b[2]) score += 1; // mismo nombre fino
        return score;
    }

    // ============================================================
    // Normalizacion de cada pieza (tolera como lo escribio cada formulario a lo largo del tiempo)
    // ============================================================

    /// <summary>
    /// "Double"/"doble"/"DBL" -> "doble". El tarifario viejo y la ficha nueva escriben distinto la misma
    /// habitacion; sin esto, la misma habitacion generaria dos memorias de precio.
    /// </summary>
    public static string NormalizeRoomType(string? roomType)
    {
        var raw = TextNormalizer.NormalizeForCatalog(roomType);
        return raw switch
        {
            "single" or "sgl" or "simple" or "individual" => "simple",
            "double" or "dbl" or "doble" or "matrimonial" => "doble",
            "twin" or "twn" => "twin",
            "triple" or "tpl" => "triple",
            "quadruple" or "quad" or "cuadruple" => "cuadruple",
            "family" or "familiar" => "familiar",
            "suite" or "ste" => "suite",
            _ => raw
        };
    }

    /// <summary>
    /// Los codigos de la hoteleria ("BB", "HB") y el castellano ("Desayuno") caen en el mismo valor.
    /// RO = room only, BB = bed &amp; breakfast, HB = half board, FB = full board, AI = all inclusive.
    /// </summary>
    public static string NormalizeMealPlan(string? mealPlan)
    {
        var raw = TextNormalizer.NormalizeForCatalog(mealPlan);
        return raw switch
        {
            "ro" or "solo alojamiento" or "solo aloj." or "solo aloj" or "sin comidas" => "solo_alojamiento",
            "bb" or "desayuno" or "con desayuno" or "breakfast" => "desayuno",
            "hb" or "media pension" or "half board" => "media_pension",
            "fb" or "pension completa" or "full board" => "pension_completa",
            "ai" or "all inclusive" or "todo incluido" => "todo_incluido",
            _ => raw
        };
    }

    /// <summary>"Economy"/"turista" -> "economica". Cabinas de aereo.</summary>
    public static string NormalizeCabin(string? cabinClass)
    {
        var raw = TextNormalizer.NormalizeForCatalog(cabinClass);
        return raw switch
        {
            "economy" or "economica" or "turista" or "eco" => "economica",
            "premium economy" or "premium" or "economica premium" => "economica_premium",
            "business" or "ejecutiva" or "clase ejecutiva" => "ejecutiva",
            "first" or "first class" or "primera" or "primera clase" => "primera",
            _ => raw
        };
    }

    // ============================================================
    // Etiquetas para mostrar (criollo, las arma el motor)
    // ============================================================

    /// <summary>"doble" + "desayuno" + "Superior" -> "Doble Superior con desayuno".</summary>
    private static string BuildHotelLabel(string roomKey, string boardKey, string? fineName)
    {
        var room = RoomLabel(roomKey);
        var fine = string.IsNullOrWhiteSpace(fineName) ? string.Empty : CapitalizeWords(fineName.Trim());
        var board = BoardLabel(boardKey);

        var head = string.Join(' ', new[] { room, fine }.Where(part => part.Length > 0));
        if (head.Length == 0) return board;      // solo regimen: "Con desayuno"
        if (board.Length == 0) return head;      // solo habitacion: "Doble Superior"

        // "Doble Superior" + "con desayuno" (el regimen se escribe en minuscula al ir pegado atras).
        return $"{head} {char.ToLowerInvariant(board[0])}{board[1..]}";
    }

    private static string RoomLabel(string roomKey) => roomKey switch
    {
        "simple" => "Simple",
        "doble" => "Doble",
        "twin" => "Twin",
        "triple" => "Triple",
        "cuadruple" => "Cuádruple",
        "familiar" => "Familiar",
        "suite" => "Suite",
        "" => string.Empty,
        _ => CapitalizeWords(roomKey)
    };

    private static string BoardLabel(string boardKey) => boardKey switch
    {
        "solo_alojamiento" => "Sin comidas",
        "desayuno" => "Con desayuno",
        "media_pension" => "Con media pensión",
        "pension_completa" => "Con pensión completa",
        "todo_incluido" => "Con todo incluido",
        "" => string.Empty,
        _ => CapitalizeWords(boardKey.Replace('_', ' '))
    };

    private static string CabinLabel(string cabinKey) => cabinKey switch
    {
        "economica" => "Económica",
        "economica_premium" => "Económica premium",
        "ejecutiva" => "Ejecutiva",
        "primera" => "Primera",
        _ => CapitalizeWords(cabinKey.Replace('_', ' '))
    };

    /// <summary>"vista al mar" -> "Vista Al Mar". Deja las palabras con mayuscula inicial, sin tocar el resto.</summary>
    private static string CapitalizeWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var capitalized = words.Select(word =>
            word.Length == 1 ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', capitalized);
    }
}
