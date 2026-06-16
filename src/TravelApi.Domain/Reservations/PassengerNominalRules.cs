using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-031 v2.1 (2026-06-15): fuente UNICA de "que datos nominales de pasajeros exige cada tipo de
/// servicio para poder RESOLVERSE/EMITIRSE". Antes los nombres se exigian al pasar de Presupuesto
/// a En gestion (el cliente acepto); esa exigencia se MOVIO a este punto, mas tarde y mas preciso:
/// los nombres legales recien son obligatorios cuando se compromete plata/cupo con el operador.
///
/// <para><b>Cambio v1 -&gt; v2.1 (B2, cambio SEMANTICO):</b> en v1 el universo era la CANTIDAD
/// DECLARADA de la reserva (<c>AdultCount + ChildCount + InfantCount</c>) y se validaban SOLO los
/// primeros N pasajeros por Id (recorte). En v2.1 el universo es el <b>SET DEL SERVICIO</b> (la lista
/// concreta de pasajeros que viajan en ESE servicio) y se valida <b>TODO el set, sin recorte</b>. El
/// "cuantos" es <c>serviceSet.Count</c>; ya no existe <c>declaredPax</c> dentro del helper.</para>
///
/// <para>Quien arma el set vive en la capa de infraestructura (BookingService/ReservaService), que
/// consulta <c>PassengerServiceAssignment</c>: servicio CON asignaciones =&gt; set = esos pasajeros;
/// servicio SIN asignaciones =&gt; set = TODOS los pasajeros de la reserva (default seguro). La
/// cantidad propia del servicio (HotelBooking.Adults, etc.) NUNCA achica el set. Este helper recibe
/// el set YA RESUELTO y no sabe nada de asignaciones ni de cantidades.</para>
///
/// <para>Regla por tipo (decision del dueno, ADR-031 §5), sobre el SET COMPLETO:</para>
/// <list type="bullet">
/// <item><b>Aereo</b>: nombre + documento (tipo y numero) de TODOS los del set.
///   (Sin fecha de nacimiento: eso es APIS/check-in, fuera de este gate.)</item>
/// <item><b>Asistencia</b>: nombre + documento + fecha de nacimiento de TODOS los del set (la prima
///   depende de la edad).</item>
/// <item><b>Hotel / Traslado</b>: solo el TITULAR del set con nombre (documento NO obligatorio).</item>
/// <item><b>Paquete / Generico</b>: nombre de TODOS los del set (sin documento).</item>
/// </list>
///
/// <para>Clase PURA (sin EF, sin DB): se testea sin Postgres, igual que
/// <see cref="ServiceResolutionRules"/>. El llamador es responsable de resolver y pasar el set.</para>
///
/// <para><b>Seguridad</b>: los mensajes NUNCA incluyen el numero de documento (dato sensible).
/// Solo dicen "falta documento de N pasajero(s)".</para>
/// </summary>
public static class PassengerNominalRules
{
    /// <summary>
    /// Tipos de servicio para los que este gate define una regla. Es un enum y no el string
    /// libre de cada entidad porque los 5 servicios tipados (FlightSegment, HotelBooking, etc.)
    /// NO tienen un campo "ServiceType" textual comparable; cada call-site sabe que tipo esta
    /// resolviendo y pasa el valor correcto.
    /// </summary>
    public enum ServiceKind
    {
        Flight,
        Hotel,
        Transfer,
        Assistance,
        Package,
        Generic
    }

    /// <summary>
    /// ADR-031 v2.1 (§3.2): resuelve el SET de un servicio a partir de UNA sola regla deterministica.
    /// Es PURA (no toca EF): la capa de infraestructura ya leyo de la DB las dos colecciones y las pasa.
    ///
    /// <list type="number">
    /// <item>Servicio CON asignaciones (<paramref name="assignedPassengerIds"/> no vacio) =&gt; SET =
    ///   exactamente los pasajeros de la reserva cuyo Id esta asignado.</item>
    /// <item>Servicio SIN asignaciones =&gt; SET = TODOS los pasajeros de la reserva (default seguro).</item>
    /// </list>
    ///
    /// <para>La cantidad propia del servicio (HotelBooking.Adults, etc.) NUNCA entra aca: el subconjunto
    /// se expresa SOLO asignando. Si el agente no asigno a nadie, el set es toda la reserva y el gate
    /// pide TODOS los nombres (de mas, nunca de menos). Tener esta seleccion en un solo lugar puro
    /// garantiza que el gate, el voucher y el preview del front resuelvan el set identico.</para>
    /// </summary>
    public static IReadOnlyList<Passenger> ResolveServiceSet(
        IReadOnlyList<Passenger> reservaPassengers,
        IReadOnlyCollection<int> assignedPassengerIds)
    {
        ArgumentNullException.ThrowIfNull(reservaPassengers);
        ArgumentNullException.ThrowIfNull(assignedPassengerIds);

        // Sin asignaciones explicitas: el servicio es para TODA la reserva (default seguro).
        if (assignedPassengerIds.Count == 0)
        {
            return reservaPassengers;
        }

        // Con asignaciones: solo los pasajeros de la reserva que figuran asignados. Si una asignacion
        // apunta a un pasajero que ya no existe en la reserva, simplemente no entra (puede dejar el set
        // vacio -> los Check* lo rechazan con mensaje accionable, NO se cae a "todos").
        var assignedIdSet = assignedPassengerIds is HashSet<int> alreadyHashSet
            ? alreadyHashSet
            : new HashSet<int>(assignedPassengerIds);

        return reservaPassengers
            .Where(passenger => assignedIdSet.Contains(passenger.Id))
            .ToList();
    }

    /// <summary>
    /// Define el TITULAR (lead passenger) del SET de forma deterministica: el primer
    /// <see cref="Passenger"/> por <c>Id</c> ascendente DEL SET. El modelo no tiene campo de titular;
    /// el Id es autoincremental e inmutable, asi que esta definicion es estable y testeable.
    /// Front y back DEBEN usar esta misma definicion para no contradecirse. Si el servicio no tiene
    /// asignaciones, el set = toda la reserva y el titular coincide con el de v1.
    /// </summary>
    public static Passenger? GetLeadPassenger(IReadOnlyList<Passenger> serviceSet)
    {
        ArgumentNullException.ThrowIfNull(serviceSet);
        return serviceSet
            .OrderBy(passenger => passenger.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Valida que el SET del servicio tenga cargados los datos nominales que exige el tipo. Lanza
    /// <see cref="InvalidOperationException"/> con un mensaje accionable (en espanol, sin el numero de
    /// documento) si falta algo. Si no falta nada, no hace nada.
    /// </summary>
    public static void EnsureCovered(IReadOnlyList<Passenger> serviceSet, ServiceKind serviceKind)
    {
        var missingMessage = GetMissingMessage(serviceSet, serviceKind);
        if (missingMessage != null)
        {
            throw new InvalidOperationException(missingMessage);
        }
    }

    /// <summary>
    /// Forma PURA para el preview/front: devuelve el mensaje de lo que falta (sin datos sensibles),
    /// o <c>null</c> si la cobertura nominal esta completa para ese tipo de servicio sobre ese set.
    /// Comparte la misma logica que <see cref="EnsureCovered"/> para que back y front nunca difieran.
    /// </summary>
    public static string? GetMissing(IReadOnlyList<Passenger> serviceSet, ServiceKind serviceKind)
        => GetMissingMessage(serviceSet, serviceKind);

    /// <summary>
    /// ADR-031 v2.1: indica si UN pasajero ya tiene los datos obligatorios para que un servicio de
    /// <paramref name="serviceKind"/> lo acepte. Pura, sin DB. La usa el contrato del front (que muestra
    /// "X de N" y resalta a quien le falta) para no reimplementar la matriz de campos por tipo. NUNCA
    /// expone el numero de documento: devuelve solo true/false.
    ///
    /// <para>Para Hotel/Traslado solo importa el titular del set; un pasajero NO titular siempre cuenta
    /// como "completo" para esos tipos (no se le pide nada). Para los demas tipos, todos cuentan.</para>
    /// </summary>
    public static bool PassengerHasRequiredData(
        Passenger passenger, ServiceKind serviceKind, bool isLeadOfSet)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        switch (serviceKind)
        {
            case ServiceKind.Hotel:
            case ServiceKind.Transfer:
                // Solo se le pide nombre al TITULAR del set; al resto, nada.
                if (!isLeadOfSet) return true;
                return !string.IsNullOrWhiteSpace(passenger.FullName);

            case ServiceKind.Flight:
                return HasRequiredFields(passenger, requireDocument: true, requireBirthDate: false);

            case ServiceKind.Assistance:
                return HasRequiredFields(passenger, requireDocument: true, requireBirthDate: true);

            case ServiceKind.Package:
            case ServiceKind.Generic:
                return HasRequiredFields(passenger, requireDocument: false, requireBirthDate: false);

            default:
                throw new ArgumentOutOfRangeException(nameof(serviceKind), serviceKind, "Tipo de servicio desconocido.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Implementacion
    // ---------------------------------------------------------------------------------------

    private static string? GetMissingMessage(IReadOnlyList<Passenger> serviceSet, ServiceKind serviceKind)
    {
        ArgumentNullException.ThrowIfNull(serviceSet);

        return serviceKind switch
        {
            // Hotel y Traslado: alcanza con el titular DEL SET cargado (nombre). Documento NO obligatorio.
            ServiceKind.Hotel => CheckLeadHasName(serviceSet, "confirmar el hotel"),
            ServiceKind.Transfer => CheckLeadHasName(serviceSet, "confirmar el traslado"),

            // Aereo: nombre + documento (tipo y numero) de TODOS los del set.
            ServiceKind.Flight => CheckAllInSet(
                serviceSet,
                requireDocument: true,
                requireBirthDate: false,
                actionLabel: "emitir este vuelo"),

            // Asistencia: nombre + documento + fecha de nacimiento de TODOS los del set.
            ServiceKind.Assistance => CheckAllInSet(
                serviceSet,
                requireDocument: true,
                requireBirthDate: true,
                actionLabel: "emitir la asistencia"),

            // Paquete y Generico: nombre de TODOS los del set (sin documento).
            ServiceKind.Package => CheckAllInSet(
                serviceSet,
                requireDocument: false,
                requireBirthDate: false,
                actionLabel: "confirmar el paquete"),
            ServiceKind.Generic => CheckAllInSet(
                serviceSet,
                requireDocument: false,
                requireBirthDate: false,
                actionLabel: "confirmar el servicio"),

            _ => throw new ArgumentOutOfRangeException(nameof(serviceKind), serviceKind, "Tipo de servicio desconocido.")
        };
    }

    /// <summary>
    /// Regla Hotel/Traslado: el SET no puede estar vacio y su titular debe tener nombre no vacio.
    /// </summary>
    private static string? CheckLeadHasName(IReadOnlyList<Passenger> serviceSet, string actionLabel)
    {
        // Set vacio = no hay pasajeros que validar. No deberia ocurrir (el set por defecto es toda
        // la reserva, que paso Budget -> InManagement con >=1 pasajero); si llega vacio es porque las
        // asignaciones apuntan a pasajeros borrados -> se rechaza, no se "cae" a todos (ADR §5.2).
        if (serviceSet.Count == 0)
        {
            return $"No se puede {actionLabel} sin pasajeros: el servicio no tiene pasajeros para validar.";
        }

        var lead = GetLeadPassenger(serviceSet);
        if (lead == null || string.IsNullOrWhiteSpace(lead.FullName))
        {
            return $"Falta el nombre del titular para {actionLabel}.";
        }

        return null;
    }

    /// <summary>
    /// Regla "TODOS los del SET": el set no puede estar vacio y CADA pasajero del set debe tener los
    /// campos obligatorios. v2.1 (B2): NO hay recorte por cantidad declarada — se valida la lista
    /// completa. Si el set tiene 3 y uno esta incompleto, falla por ese uno.
    /// </summary>
    private static string? CheckAllInSet(
        IReadOnlyList<Passenger> serviceSet,
        bool requireDocument,
        bool requireBirthDate,
        string actionLabel)
    {
        // Set vacio = error accionable (mismo motivo que en CheckLeadHasName).
        if (serviceSet.Count == 0)
        {
            return $"No se puede {actionLabel} sin pasajeros: el servicio no tiene pasajeros para validar.";
        }

        // Validamos TODO el set (sin recorte). Contamos cuantos estan incompletos y, ademas, QUE campos
        // faltan REALMENTE en el conjunto. El mensaje debe decir exactamente lo que falta: si el agente
        // ya cargo el nombre pero le falta el documento, no tiene sentido decirle "falta nombre" (ese fue
        // el bug reportado: el aereo decia "Faltan nombre y documento" aunque el nombre estuviera cargado).
        var incompletePassengers = 0;
        var anyNameMissing = false;
        var anyDocumentMissing = false;
        var anyBirthDateMissing = false;

        foreach (var passenger in serviceSet)
        {
            if (HasRequiredFields(passenger, requireDocument, requireBirthDate))
            {
                continue;
            }

            incompletePassengers++;

            if (string.IsNullOrWhiteSpace(passenger.FullName))
            {
                anyNameMissing = true;
            }
            if (requireDocument
                && (string.IsNullOrWhiteSpace(passenger.DocumentType)
                    || string.IsNullOrWhiteSpace(passenger.DocumentNumber)))
            {
                anyDocumentMissing = true;
            }
            if (requireBirthDate && passenger.BirthDate == null)
            {
                anyBirthDateMissing = true;
            }
        }

        if (incompletePassengers > 0)
        {
            return BuildMissingDataMessage(
                incompletePassengers, anyNameMissing, anyDocumentMissing, anyBirthDateMissing, actionLabel);
        }

        return null;
    }

    private static bool HasRequiredFields(Passenger passenger, bool requireDocument, bool requireBirthDate)
    {
        if (string.IsNullOrWhiteSpace(passenger.FullName))
        {
            return false;
        }

        // El aereo exige tipo Y numero de documento; la asistencia exige el numero (la fecha de
        // nacimiento se chequea aparte). Para no exponer el numero, solo se valida que no esten vacios.
        if (requireDocument
            && (string.IsNullOrWhiteSpace(passenger.DocumentType)
                || string.IsNullOrWhiteSpace(passenger.DocumentNumber)))
        {
            return false;
        }

        if (requireBirthDate && passenger.BirthDate == null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Arma el mensaje accionable nombrando SOLO los campos que faltan de verdad en el set, no todos los
    /// que el tipo podria exigir. Asi, si el nombre ya esta cargado y solo falta el documento, el mensaje
    /// dice "Falta el documento ..." y no "Faltan nombre y documento ..." (que confundia al agente
    /// haciendole creer que el pasajero no estaba cargado). NUNCA incluye el numero de documento (dato
    /// sensible): solo cuenta cuantos pasajeros estan incompletos y que campos faltan.
    /// </summary>
    private static string BuildMissingDataMessage(
        int affectedPassengers,
        bool nameMissing,
        bool documentMissing,
        bool birthDateMissing,
        string actionLabel)
    {
        // Listamos solo los campos realmente faltantes, en orden estable.
        var missingFieldNames = new List<string>(capacity: 3);
        if (nameMissing) missingFieldNames.Add("nombre");
        if (documentMissing) missingFieldNames.Add("documento");
        if (birthDateMissing) missingFieldNames.Add("fecha de nacimiento");

        // Defensa: si por algun motivo no se marco ningun campo (no deberia pasar cuando hay incompletos),
        // caemos a un mensaje generico para no devolver un texto roto.
        if (missingFieldNames.Count == 0)
        {
            return $"Faltan datos de {affectedPassengers} pasajero(s) para {actionLabel}.";
        }

        var fieldList = JoinWithSpanishConjunction(missingFieldNames);
        // "Falta el nombre" (singular) vs "Faltan nombre y documento" (plural): elegimos el verbo segun
        // cuantos campos falten, para que el castellano suene natural.
        var verb = missingFieldNames.Count == 1 ? "Falta el" : "Faltan";

        return $"{verb} {fieldList} de {affectedPassengers} pasajero(s) para {actionLabel}.";
    }

    /// <summary>
    /// Une una lista de campos con comas y "y" antes del ultimo, al estilo castellano:
    /// ["nombre"] -> "nombre"; ["nombre","documento"] -> "nombre y documento";
    /// ["nombre","documento","fecha de nacimiento"] -> "nombre, documento y fecha de nacimiento".
    /// </summary>
    private static string JoinWithSpanishConjunction(IReadOnlyList<string> items)
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        var allButLast = string.Join(", ", items.Take(items.Count - 1));
        var last = items[items.Count - 1];
        return $"{allButLast} y {last}";
    }
}
