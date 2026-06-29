using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Security;

/// <summary>
/// Data-exposure hardening (2026-06-29): los mensajes de las excepciones de NEGOCIO del flujo de
/// cancelacion llegan al usuario TAL CUAL via <see cref="GlobalExceptionHandler"/> (copia
/// <c>Message</c> a <c>ProblemDetails.Detail</c> en el 409 de
/// <see cref="BusinessInvariantViolationException"/> y en el 400 de <c>ValidationException</c>).
///
/// <para>El usuario es un agente de viajes NO tecnico: JAMAS debe ver nombres internos de la maquina
/// de estados (valores del enum <c>BookingCancellationStatus</c> como <c>Drafted</c> /
/// <c>AwaitingFiscalConfirmation</c>), nombres de operaciones internas
/// (<c>ForceArcaConfirmation</c> / <c>EditLiquidation</c>), nombres de campos internos
/// (<c>DebitNoteStatus</c>, <c>CreditNoteInvoiceId</c>, <c>OverrideReason</c>, ...) ni jerga en
/// ingles (<c>endpoint</c>). El estado <c>Reserva.Status</c> tambien es un enum-string interno en
/// ingles (<c>Confirmed</c>, <c>Traveling</c>, ...), asi que tampoco debe interpolarse.</para>
///
/// <para>El namespace NO contiene "Integration" (entra en la suite no-Docker) y el nombre contiene
/// "Unit" para el filtro de CI. Son tests puros: uno invoca el guard estatico real y el otro escanea
/// el codigo fuente de los services del flujo de cancelacion.</para>
/// </summary>
public class CancellationErrorMessageLeakUnitTests
{
    // Identificadores internos que NUNCA deben aparecer en el Message de una excepcion de negocio de
    // cara al usuario. Case-sensitive (Ordinal): son identificadores C#/enum en PascalCase, NO palabras
    // en espanol (p.ej. "estado" no colisiona con "Status"; "abortar" no colisiona con "Aborted";
    // "estimado" no colisiona con "Estimated"; "pendiente" no colisiona con "Pending").
    private static readonly string[] ForbiddenInternalTokens =
    {
        // Valores del enum BookingCancellationStatus (TODOS, incidente fundacional de fuga de estados).
        "Drafted", "AwaitingFiscalConfirmation", "AwaitingOperatorRefund",
        "ClientCreditApplied", "Closed", "AbandonedByOperator", "Aborted", "ArcaRejected",
        "RequiresManualReview", "ManualReviewPending", "ManualReviewApproved", "ManualReviewRejected",
        // Valores del enum PenaltyStatus.
        "Estimated", "Confirmed", "Waived",
        // Valores del enum DebitNoteStatus.
        "NotApplicable", "Pending", "Issued", "Failed", "ManualReview",
        // Valores del enum-string Reserva.Status (estado operativo interno en ingles).
        "InManagement", "Traveling", "Quotation", "PendingOperatorRefund",
        // Nombres de operaciones internas.
        "ForceArcaConfirmation", "EditLiquidation",
        // Jerga tecnica en ingles (palabra suelta).
        "endpoint",
        // Nombres de campos internos (incluye ids: incidente fundacional de fuga de ids).
        "DebitNoteStatus", "PenaltyStatus", "CreditNoteInvoiceId", "DebitNoteInvoiceId",
        "PartialCreditNoteApprovalRequestId", "FiscalSnapshot", "ManualJustification",
        "OverrideReason", "IsAdminOverride", "EnablePartialCreditNotes",
        "PublicId", "SupplierId", "ReservaId",
        // Interpolacion de cualquier *.Status (ej. {bc.Status}, {targetReserva.Status}).
        ".Status}",
    };

    // Jerga en ingles que se cuela en minuscula. Match como PALABRA SUELTA (case-insensitive) para no
    // dar falsos positivos con espanol: "null" no matchea dentro de "anular"; "gross" no existe en
    // espanol. Cubre los terminos que el barrido quito (allocate/gross) + tipicos de respuestas crudas
    // (payload/null/undefined) que jamas deben llegar al usuario.
    private static readonly Regex ForbiddenJargonRegex = new(
        @"\b(allocate|gross|payload|null|undefined)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // GUID crudo: si un futuro $"... {bc.PublicId} ..." emite un GUID en runtime, o alguien hardcodea uno,
    // no debe llegar al usuario. (Sobre el codigo fuente casi nunca aparece literal; el guard real para el
    // caso runtime es IdInterpolationRegex de abajo, que ve la interpolacion antes de que se materialice.)
    private static readonly Regex RawGuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    // Interpolacion de un id interno dentro del mensaje: {bc.PublicId}, {x.SupplierId}, {reserva.Id}, ...
    // El VALOR en runtime es un GUID/int interno -> fuga. Se chequea sobre el texto fuente del mensaje.
    private static readonly Regex IdInterpolationRegex = new(
        @"\{[^}]*Id[^}]*\}", RegexOptions.Compiled);

    // Reune TODAS las fugas de un mensaje (tokens internos + jerga + GUID + interpolacion de id).
    private static IEnumerable<string> CollectMessageLeaks(string message)
    {
        foreach (var token in ForbiddenInternalTokens)
        {
            if (message.Contains(token, StringComparison.Ordinal))
                yield return $"nombre interno '{token}'";
        }

        var jargon = ForbiddenJargonRegex.Match(message);
        if (jargon.Success)
            yield return $"jerga en ingles '{jargon.Value}'";

        if (RawGuidRegex.IsMatch(message))
            yield return "un GUID crudo";

        var idHole = IdInterpolationRegex.Match(message);
        if (idHole.Success)
            yield return $"una interpolacion de id interno '{idHole.Value.Trim()}'";
    }

    // Services del flujo de cancelacion cuyos mensajes de negocio llegan al usuario por ProblemDetails.
    // (OperationalFinanceSettingsService queda FUERA a proposito: es la pantalla de configuracion del
    //  Admin, donde nombrar el flag incompatible es la informacion accionable que el admin necesita.)
    private static readonly string[] CancellationFlowServiceFiles =
    {
        "BookingCancellationService.cs",
        "OperatorRefundService.cs",
        "ClientCreditService.cs",
        "SupplierCreditService.cs",
        "FiscalLiquidationCalculator.cs",
    };

    [Fact]
    public void CancellationFlow_BusinessExceptionMessages_DoNotLeakInternalNamesOrJargon()
    {
        var offenders = new List<string>();

        foreach (var fileName in CancellationFlowServiceFiles)
        {
            var path = ResolveServiceFilePath(fileName);
            var source = File.ReadAllText(path);

            foreach (var message in ExtractBusinessExceptionMessages(source))
            {
                foreach (var leak in CollectMessageLeaks(message))
                    offenders.Add($"{fileName}: el mensaje '{Collapse(message)}' filtra {leak}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Hay mensajes de excepcion de negocio que filtran nombres internos o jerga al usuario:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// El guard estatico real (sin DB) <see cref="BookingCancellationService.EnsureConceptNotLockedByDebitNote"/>
    /// tira <see cref="BusinessInvariantViolationException"/> cuando se intenta reclasificar el concepto con una
    /// Nota de Debito ya en juego. Verifica que (a) el codigo de invariante se conserva y (b) el Message ya NO
    /// interpola el enum interno <c>DebitNoteStatus</c>.
    /// </summary>
    [Fact]
    public void EnsureConceptNotLockedByDebitNote_Throws_WithCleanMessage_AndPreservedCode()
    {
        var bc = new BookingCancellation
        {
            ConceptKind = CancellationConceptKind.AgencyManagementFee,
            // ND ya emitida -> reclasificar a otro concepto esta bloqueado.
            DebitNoteStatus = DebitNoteStatus.Issued,
        };

        var exception = Assert.Throws<BusinessInvariantViolationException>(() =>
            BookingCancellationService.EnsureConceptNotLockedByDebitNote(
                bc, CancellationConceptKind.OperatorPenaltyPassThrough));

        // El codigo NO cambia (lo consumen el front y otros tests).
        Assert.Equal("INV-ADR013-002", exception.InvariantCode);

        // El Message no filtra nada interno (mismo guard reforzado que el escaneo de fuente).
        var leaks = CollectMessageLeaks(exception.Message).ToList();
        Assert.True(leaks.Count == 0,
            "El mensaje del guard filtra al usuario: " + string.Join(", ", leaks));
    }

    /// <summary>
    /// El guard TIENE dientes: cada categoria de fuga (enum de estado, id interno, interpolacion de id,
    /// jerga en ingles, GUID crudo) debe ser detectada. Si alguien debilita <see cref="CollectMessageLeaks"/>
    /// este test lo rompe. Tambien fija que el copy amigable legitimo (espanol de negocio) NO da falso positivo.
    /// </summary>
    [Theory]
    // Enum de BookingCancellationStatus / PenaltyStatus / DebitNoteStatus (incidente fundacional).
    [InlineData("Solo se puede confirmar en estado Drafted.", true)]
    [InlineData("La cancelacion ya esta Closed.", true)]
    [InlineData("El BC quedo Aborted.", true)]
    [InlineData("La penalidad esta Estimated.", true)]
    [InlineData("La ND quedo Issued.", true)]
    // Ids internos + interpolacion de id (incidente fundacional de fuga de ids).
    [InlineData("No se encontro el BC abc PublicId xyz.", true)]
    [InlineData("La reserva {bc.ReservaId} no existe.", true)]
    [InlineData("El operador {refund.SupplierId} no coincide.", true)]
    // GUID crudo.
    [InlineData("La reserva 3fa85f64-5717-4562-b3fc-2c963f66afa6 ya tiene cancelacion.", true)]
    // Jerga en ingles suelta.
    [InlineData("No se puede allocate el reintegro.", true)]
    [InlineData("El monto gross no puede ser negativo.", true)]
    // Copy amigable legitimo (NO debe dar falso positivo): palabras espanolas que contienen los tokens.
    [InlineData("Esta cancelación ya no se puede abortar en este momento.", false)]
    [InlineData("Anulá la Nota de Débito antes de reclasificar.", false)]
    [InlineData("La cancelación queda pendiente de revisión manual.", false)]
    [InlineData("El monto estimado supera el total. Pasala a En gestión primero.", false)]
    public void CollectMessageLeaks_DetectsEachLeakCategory_AndIgnoresLegitimateSpanish(
        string message, bool shouldLeak)
    {
        var leaks = CollectMessageLeaks(message).ToList();
        Assert.Equal(shouldLeak, leaks.Count > 0);
    }

    /// <summary>
    /// Pin de los 7 mensajes que la revision de data-exposure pidio reescribir: el copy amigable nuevo
    /// debe estar presente (asi un futuro cambio que reintroduzca el enum rompe el test), y los codigos de
    /// invariante asociados deben seguir existiendo en el archivo.
    /// </summary>
    [Fact]
    public void RewrittenCancellationMessages_ArePresent_AndCodesUnchanged()
    {
        var source = File.ReadAllText(ResolveServiceFilePath("BookingCancellationService.cs"));

        // Copy amigable nuevo (los 7 puntos del pedido + extras saneados).
        var expectedFriendlyPhrases = new[]
        {
            "ya tiene una cancelación en curso.",
            "ya no se puede confirmar porque cambió de estado. Actualizá la página.",
            "ya no se puede abortar en este momento.",
            "solo se puede reabrir por un reembolso tardío",
            "Esta acción no está disponible para el estado actual de la cancelación.",
            "ya no se puede editar porque cambió de estado. Actualizá la página.",
            "Esta acción emite la ND",
        };
        foreach (var phrase in expectedFriendlyPhrases)
            Assert.Contains(phrase, source, StringComparison.Ordinal);

        // Los codigos de invariante NO cambian (solo cambio el texto Detail).
        var expectedCodes = new[]
        {
            "INV-081", "INV-093", "INV-100", "INV-118", "INV-120",
            "INV-FC1.3-007", "INV-FC1.3-002", "INV-FC1.3-004", "INV-FC1.3-005",
            "INV-ADR014-002", "INV-ADR013-002", "INV-ADMIN-SELFAUTH",
        };
        foreach (var code in expectedCodes)
            Assert.Contains($"\"{code}\"", source, StringComparison.Ordinal);
    }

    // =====================================================================
    // Helpers de parsing del codigo fuente.
    // =====================================================================

    // Extrae el PRIMER argumento (el mensaje de cara al usuario) de cada construccion de
    // BusinessInvariantViolationException / ValidationException. Ignora los named args finales
    // (invariantCode:/constraintName:) y los comentarios de linea dentro de la llamada, de modo que
    // un comentario que mencione un enum NO cuente como fuga.
    private static IEnumerable<string> ExtractBusinessExceptionMessages(string source)
    {
        var markers = new[]
        {
            "new BusinessInvariantViolationException(",
            "new ValidationException(",
        };

        foreach (var marker in markers)
        {
            var searchFrom = 0;
            while (true)
            {
                var markerIndex = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                if (markerIndex < 0)
                    break;

                var openParen = markerIndex + marker.Length - 1;
                var closeParen = FindMatchingCloseParen(source, openParen);
                searchFrom = closeParen + 1;

                if (closeParen <= openParen)
                    continue;

                var callBody = source.Substring(openParen + 1, closeParen - openParen - 1);
                yield return ExtractMessagePortion(callBody);
            }
        }
    }

    // Camina string-aware desde el '(' de apertura (depth=1) hasta el ')' que lo cierra.
    // Respeta literales de string ("..." y $"...") para no contar parentesis dentro del texto.
    private static int FindMatchingCloseParen(string source, int openParenIndex)
    {
        var depth = 1;
        var i = openParenIndex + 1;
        var inString = false;

        while (i < source.Length && depth > 0)
        {
            var c = source[i];
            if (inString)
            {
                if (c == '\\') { i += 2; continue; } // escape dentro del string
                if (c == '"') inString = false;
            }
            else
            {
                if (c == '"') inString = true;
                else if (c == '(') depth++;
                else if (c == ')') depth--;
            }
            i++;
        }

        return i - 1;
    }

    // Devuelve solo la porcion del mensaje: corta en el primer named arg final y saca comentarios de linea.
    private static string ExtractMessagePortion(string callBody)
    {
        var withoutComments = StripLineComments(callBody);

        var cutMarkers = new[] { "invariantCode:", "constraintName:" };
        var cutIndex = withoutComments.Length;
        foreach (var cutMarker in cutMarkers)
        {
            var index = withoutComments.IndexOf(cutMarker, StringComparison.Ordinal);
            if (index >= 0 && index < cutIndex)
                cutIndex = index;
        }

        return withoutComments.Substring(0, cutIndex);
    }

    private static string StripLineComments(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Los mensajes de este flujo no contienen "//" dentro de los literales, asi que cortar en
            // el primer "//" elimina el comentario sin mutilar el texto del mensaje.
            var commentIndex = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (commentIndex >= 0)
                lines[i] = lines[i].Substring(0, commentIndex);
        }
        return string.Join("\n", lines);
    }

    private static string Collapse(string text) =>
        string.Join(" ", text.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries));

    private static string ResolveServiceFilePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src", "TravelApi.Infrastructure", "Services", fileName);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No se encontro {fileName} subiendo desde {AppContext.BaseDirectory}. " +
            "El test necesita el codigo fuente para escanear los mensajes.");
    }
}
