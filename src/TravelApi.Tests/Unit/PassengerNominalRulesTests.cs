using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-031 v2.1: tests de la clase PURA PassengerNominalRules (sin DB). En v2.1 el helper valida EL SET
/// del servicio (lista de pasajeros), NO la cantidad declarada de la reserva, y NO recorta a los primeros
/// N por Id (B2, cambio semantico). Cubren: la matriz por tipo sobre el set, el titular del set (primer
/// Id), la resolucion del set (asignaciones vs default), el set vacio, la regresion B2, y que los mensajes
/// NUNCA contengan el numero de documento.
/// </summary>
public class PassengerNominalRulesTests
{
    private static Passenger Pax(int id, string name, string? docType = null, string? docNumber = null, DateTime? birthDate = null)
        => new()
        {
            Id = id, ReservaId = 1, FullName = name,
            DocumentType = docType, DocumentNumber = docNumber, BirthDate = birthDate
        };

    // El "set" del servicio en v2.1 es directamente una lista de pasajeros.
    private static IReadOnlyList<Passenger> Set(params Passenger[] passengers) => passengers;

    // ===================== Titular del SET =====================

    [Fact]
    public void GetLeadPassenger_ReturnsFirstByIdOfSet()
    {
        var set = Set(Pax(5, "Segundo"), Pax(2, "Primero"));
        var lead = PassengerNominalRules.GetLeadPassenger(set);
        Assert.NotNull(lead);
        Assert.Equal("Primero", lead!.FullName); // el de menor Id, no el orden de insercion
    }

    [Fact]
    public void GetLeadPassenger_EmptySet_ReturnsNull()
        => Assert.Null(PassengerNominalRules.GetLeadPassenger(Set()));

    // ===================== Resolucion del SET (asignaciones vs default) =====================

    [Fact]
    public void ResolveServiceSet_NoAssignments_ReturnsAllReservaPassengers()
    {
        var all = new List<Passenger> { Pax(1, "Uno"), Pax(2, "Dos"), Pax(3, "Tres") };
        var set = PassengerNominalRules.ResolveServiceSet(all, new List<int>());
        Assert.Equal(3, set.Count);
    }

    [Fact]
    public void ResolveServiceSet_WithAssignments_ReturnsOnlyAssigned()
    {
        var all = new List<Passenger> { Pax(1, "Adulto1"), Pax(2, "Adulto2"), Pax(3, "Menor") };
        // Excursion solo para los 2 adultos.
        var set = PassengerNominalRules.ResolveServiceSet(all, new List<int> { 1, 2 });
        Assert.Equal(2, set.Count);
        Assert.DoesNotContain(set, p => p.Id == 3); // el menor NO entra
    }

    [Fact]
    public void ResolveServiceSet_AssignmentsToDeletedPassengers_ReturnsEmptySet_DoesNotFallBackToAll()
    {
        var all = new List<Passenger> { Pax(1, "Uno"), Pax(2, "Dos") };
        // Asignaciones que apuntan a ids que ya no existen en la reserva -> set vacio, NO "todos".
        var set = PassengerNominalRules.ResolveServiceSet(all, new List<int> { 99, 100 });
        Assert.Empty(set);
    }

    // ===================== Hotel / Traslado: solo titular del SET con nombre =====================

    [Theory]
    [InlineData(PassengerNominalRules.ServiceKind.Hotel)]
    [InlineData(PassengerNominalRules.ServiceKind.Transfer)]
    public void HotelTransfer_WithLeadNamed_Passes(PassengerNominalRules.ServiceKind kind)
    {
        // Set de 2; al titular le basta nombre; al resto no se le pide nada.
        var set = Set(Pax(1, "Titular"), Pax(2, "Acompanante"));
        Assert.Null(PassengerNominalRules.GetMissing(set, kind));
        PassengerNominalRules.EnsureCovered(set, kind); // no lanza
    }

    [Theory]
    [InlineData(PassengerNominalRules.ServiceKind.Hotel)]
    [InlineData(PassengerNominalRules.ServiceKind.Transfer)]
    public void HotelTransfer_EmptySet_Fails(PassengerNominalRules.ServiceKind kind)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PassengerNominalRules.EnsureCovered(Set(), kind));
        Assert.Contains("sin pasajeros", ex.Message);
    }

    [Fact]
    public void Hotel_LeadWithEmptyName_Fails()
    {
        var set = Set(Pax(1, "   "));
        Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Hotel));
    }

    [Fact]
    public void Hotel_LeadOfSubsetIsNotReservaLead()
    {
        // Set = subconjunto (pasajeros 2 y 3). El titular del SET es el 2, no el 1 de la reserva.
        var set = Set(Pax(3, "Tercero"), Pax(2, "Segundo"));
        var lead = PassengerNominalRules.GetLeadPassenger(set);
        Assert.Equal("Segundo", lead!.FullName);
    }

    // ===================== Aereo: nombre + documento de TODOS los del SET =====================

    [Fact]
    public void Flight_AllInSetWithNameAndDocument_Passes()
    {
        var set = Set(
            Pax(1, "Uno", "DNI", "11111111"),
            Pax(2, "Dos", "DNI", "22222222"));
        Assert.Null(PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Flight));
    }

    [Fact]
    public void Flight_MissingDocumentNumber_Fails()
    {
        var set = Set(
            Pax(1, "Uno", "DNI", "11111111"),
            Pax(2, "Dos", "DNI", docNumber: null)); // sin numero
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Flight));
        Assert.Contains("documento", ex.Message);
    }

    [Fact]
    public void Flight_MissingDocumentType_Fails()
    {
        var set = Set(Pax(1, "Uno", docType: null, docNumber: "11111111"));
        Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Flight));
    }

    // ===================== Bug 2026-06-15: el mensaje nombra SOLO lo que falta de verdad =====================
    // El agente cargo el NOMBRE pero le falta el DOCUMENTO. El mensaje viejo decia "Faltan nombre y
    // documento", lo que hacia creer que el pasajero no estaba cargado. Ahora debe nombrar solo el documento.

    [Fact]
    public void Flight_NameLoadedButDocumentMissing_MessageMentionsOnlyDocument_NotName()
    {
        var set = Set(Pax(1, "Pasajero Con Nombre")); // tiene nombre, NO tiene documento
        var message = PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Flight);

        Assert.NotNull(message);
        Assert.Contains("documento", message);
        // Lo clave del fix: NO debe decir que falta el nombre, porque el nombre SI esta cargado.
        Assert.DoesNotContain("nombre", message);
        Assert.Contains("emitir este vuelo", message);
    }

    [Fact]
    public void Flight_NameAndDocumentBothMissing_MessageMentionsBoth()
    {
        var set = Set(Pax(1, "   ")); // sin nombre ni documento
        var message = PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Flight);

        Assert.NotNull(message);
        Assert.Contains("nombre", message);
        Assert.Contains("documento", message);
    }

    [Fact]
    public void Assistance_OnlyBirthDateMissing_MessageMentionsOnlyBirthDate()
    {
        // Nombre + documento cargados; falta SOLO la fecha de nacimiento.
        var set = Set(Pax(1, "Uno", "DNI", "11111111", birthDate: null));
        var message = PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Assistance);

        Assert.NotNull(message);
        Assert.Contains("fecha de nacimiento", message);
        Assert.DoesNotContain("nombre", message);
        // "documento" no debe aparecer como faltante (el numero esta cargado).
        Assert.DoesNotContain("falta el documento", message.ToLowerInvariant());
    }

    [Fact]
    public void Flight_DoesNotRequireBirthDate()
    {
        // El aereo NO exige fecha de nacimiento (decision del dueno: eso es APIS/check-in).
        var set = Set(Pax(1, "Uno", "DNI", "11111111", birthDate: null));
        Assert.Null(PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Flight));
    }

    [Fact]
    public void Flight_EmptySet_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(Set(), PassengerNominalRules.ServiceKind.Flight));
        Assert.Contains("sin pasajeros", ex.Message);
    }

    // ===================== B2: regresion del cambio semantico (ya NO se recorta) =====================

    [Fact]
    public void Flight_B2_ValidatesEntireSet_NoTruncationByDeclaredCount()
    {
        // SET de 3, el tercero SIN documento. En v1, con declaredPax=2, este caso PASABA (recortaba a 2).
        // En v2.1 el set completo se valida -> RECHAZO por el tercero. Esta es la prueba de que NO se recorta.
        var set = Set(
            Pax(1, "Uno", "DNI", "11111111"),
            Pax(2, "Dos", "DNI", "22222222"),
            Pax(3, "Tres sin doc")); // incompleto
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Flight));
        Assert.Contains("documento", ex.Message);
    }

    // ===================== Asistencia: nombre + documento + fecha de nacimiento de TODOS =====================

    [Fact]
    public void Assistance_AllComplete_Passes()
    {
        var set = Set(
            Pax(1, "Uno", "DNI", "11111111", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Assistance));
    }

    [Fact]
    public void Assistance_MissingBirthDate_Fails()
    {
        var set = Set(Pax(1, "Uno", "DNI", "11111111", birthDate: null));
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Assistance));
        Assert.Contains("fecha de nacimiento", ex.Message);
    }

    // ===================== Paquete / Generico: nombre de TODOS los del SET (sin documento) =====================

    [Theory]
    [InlineData(PassengerNominalRules.ServiceKind.Package)]
    [InlineData(PassengerNominalRules.ServiceKind.Generic)]
    public void PackageGeneric_AllNamed_Passes(PassengerNominalRules.ServiceKind kind)
    {
        var set = Set(Pax(1, "Uno"), Pax(2, "Dos"));
        Assert.Null(PassengerNominalRules.GetMissing(set, kind));
    }

    [Theory]
    [InlineData(PassengerNominalRules.ServiceKind.Package)]
    [InlineData(PassengerNominalRules.ServiceKind.Generic)]
    public void PackageGeneric_MissingOneName_Fails(PassengerNominalRules.ServiceKind kind)
    {
        var set = Set(Pax(1, "Uno"), Pax(2, "   ")); // uno sin nombre
        Assert.Throws<InvalidOperationException>(() => PassengerNominalRules.EnsureCovered(set, kind));
    }

    [Theory]
    [InlineData(PassengerNominalRules.ServiceKind.Package)]
    [InlineData(PassengerNominalRules.ServiceKind.Generic)]
    public void PackageGeneric_DoesNotRequireDocument(PassengerNominalRules.ServiceKind kind)
    {
        var set = Set(Pax(1, "Uno", docType: null, docNumber: null));
        Assert.Null(PassengerNominalRules.GetMissing(set, kind));
    }

    // ===================== Excursion 2 de 3: el set chico pide solo a los suyos =====================

    [Fact]
    public void Package_SubsetOfTwoAdults_RequiresOnlyThoseTwo_NotTheMinor()
    {
        // Reserva 2A+1C. Excursion (Paquete) asignada SOLO a los 2 adultos. El menor (sin nombre) NO entra,
        // asi que el gate del paquete pasa con los 2 adultos nombrados.
        var all = new List<Passenger> { Pax(1, "Adulto1"), Pax(2, "Adulto2"), Pax(3, "   ") /* menor sin nombre */ };
        var set = PassengerNominalRules.ResolveServiceSet(all, new List<int> { 1, 2 });
        Assert.Null(PassengerNominalRules.GetMissing(set, PassengerNominalRules.ServiceKind.Package));
    }

    // ===================== PassengerHasRequiredData (contrato del front) =====================

    [Fact]
    public void PassengerHasRequiredData_HotelNonLead_AlwaysTrue()
    {
        // A un pasajero NO titular no se le pide nada en Hotel/Traslado -> completo aunque no tenga datos.
        var pax = Pax(2, "   "); // ni nombre tiene
        Assert.True(PassengerNominalRules.PassengerHasRequiredData(
            pax, PassengerNominalRules.ServiceKind.Hotel, isLeadOfSet: false));
    }

    [Fact]
    public void PassengerHasRequiredData_FlightWithoutDocument_False()
    {
        var pax = Pax(1, "Uno"); // sin documento
        Assert.False(PassengerNominalRules.PassengerHasRequiredData(
            pax, PassengerNominalRules.ServiceKind.Flight, isLeadOfSet: true));
    }

    // ===================== Seguridad: el mensaje no expone el numero de documento =====================

    [Fact]
    public void ErrorMessage_NeverContainsDocumentNumber()
    {
        var set = Set(
            Pax(1, "Uno", "DNI", "11111111"),
            Pax(2, "Dos", "DNI", docNumber: null));
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassengerNominalRules.EnsureCovered(set, PassengerNominalRules.ServiceKind.Flight));
        // El documento real del pasajero 1 (11111111) jamas debe aparecer en el mensaje.
        Assert.DoesNotContain("11111111", ex.Message);
    }
}
