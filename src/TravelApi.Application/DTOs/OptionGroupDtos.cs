namespace TravelApi.Application.DTOs;

/// <summary>
/// Opciones A/B/C (decisión #1 firmada del dueño, 2026-08-11/12): body de "resolver grupo de
/// opciones" — elige cuál de las alternativas quedó y borra las demás. <c>WinnerServiceType</c> usa
/// los mismos tokens que ya expone la API para identificar el tipo de servicio ("Hotel", "Flight",
/// "Transfer", "Package", "Assistance" — ver <see cref="TravelApi.Domain.Entities.AssignmentServiceType"/>).
/// </summary>
public record ResolveOptionGroupRequest(
    string OptionGroup,
    string WinnerServiceType,
    string WinnerServicePublicId);

/// <summary>Un servicio que se borró al resolver el grupo (etiqueta legible, sin datos de costo).</summary>
public record RemovedOptionGroupServiceDto(string ServiceType, string Label);

/// <summary>Resultado de resolver un grupo de opciones: qué quedó y qué se borró.</summary>
public record ResolveOptionGroupResultDto(
    string OptionGroup,
    string WinnerServiceType,
    Guid WinnerServicePublicId,
    string WinnerLabel,
    IReadOnlyList<RemovedOptionGroupServiceDto> RemovedServices);
