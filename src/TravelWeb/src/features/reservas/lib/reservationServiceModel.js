export const SERVICE_RECORD_KIND = Object.freeze({
  FLIGHT: "flight",
  HOTEL: "hotel",
  TRANSFER: "transfer",
  PACKAGE: "package",
  GENERIC: "generic",
});

const SPECIFIC_CREATE_ENDPOINTS = Object.freeze({
  Aereo: "flights",
  Flight: "flights",
  Hotel: "hotels",
  Traslado: "transfers",
  Transfer: "transfers",
  Paquete: "packages",
  Package: "packages",
});

const RECORD_KIND_ENDPOINTS = Object.freeze({
  [SERVICE_RECORD_KIND.FLIGHT]: "flights",
  [SERVICE_RECORD_KIND.HOTEL]: "hotels",
  [SERVICE_RECORD_KIND.TRANSFER]: "transfers",
  [SERVICE_RECORD_KIND.PACKAGE]: "packages",
});

const SERVICE_TYPE_RECORD_KINDS = Object.freeze({
  Aereo: SERVICE_RECORD_KIND.FLIGHT,
  Flight: SERVICE_RECORD_KIND.FLIGHT,
  Hotel: SERVICE_RECORD_KIND.HOTEL,
  Traslado: SERVICE_RECORD_KIND.TRANSFER,
  Transfer: SERVICE_RECORD_KIND.TRANSFER,
  Paquete: SERVICE_RECORD_KIND.PACKAGE,
  Package: SERVICE_RECORD_KIND.PACKAGE,
});

export const RECORD_KIND_DISPLAY_TYPE = Object.freeze({
  [SERVICE_RECORD_KIND.FLIGHT]: "Aereo",
  [SERVICE_RECORD_KIND.HOTEL]: "Hotel",
  [SERVICE_RECORD_KIND.TRANSFER]: "Traslado",
  [SERVICE_RECORD_KIND.PACKAGE]: "Paquete",
  [SERVICE_RECORD_KIND.GENERIC]: "Generico",
});

export function getReservationServicePublicId(service) {
  if (!service) {
    return "";
  }

  return (
    service.publicId ||
    service.PublicId ||
    service.id ||
    service.Id ||
    service.servicePublicId ||
    service.ServicePublicId ||
    ""
  );
}

function firstNonEmpty(...values) {
  return values.find((value) => typeof value === "string" && value.trim())?.trim() || "";
}

function toTimestamp(value) {
  const timestamp = Date.parse(value || "");
  return Number.isFinite(timestamp) ? timestamp : Number.MAX_SAFE_INTEGER;
}

function normalizeGenericDisplayType(service) {
  return firstNonEmpty(
    service.displayType,
    service._type,
    service.sourceKind,
    service.serviceType,
    service.productType,
    RECORD_KIND_DISPLAY_TYPE[SERVICE_RECORD_KIND.GENERIC]
  );
}

function normalizeService(rawService, recordKind, fallback) {
  const displayType =
    recordKind === SERVICE_RECORD_KIND.GENERIC
      ? normalizeGenericDisplayType(rawService)
      : RECORD_KIND_DISPLAY_TYPE[recordKind];

  return {
    ...rawService,
    recordKind,
    displayType,
    _type: displayType,
    date: firstNonEmpty(rawService.date, fallback.date),
    name: firstNonEmpty(rawService.name, fallback.name, rawService.description, displayType),
  };
}

export function normalizeReservaServices(reserva) {
  if (!reserva) {
    return [];
  }

  const flightSegments = (reserva.flightSegments || []).map((flight) => {
    const route = firstNonEmpty(
      [flight.origin, flight.destination].filter(Boolean).join(" > "),
      flight.description
    );
    const flightName = firstNonEmpty(
      [flight.airlineName || flight.airlineCode, flight.flightNumber].filter(Boolean).join(" "),
      route,
      "Vuelo"
    );

    return normalizeService(flight, SERVICE_RECORD_KIND.FLIGHT, {
      date: flight.departureTime,
      name: flightName,
    });
  });

  const hotelBookings = (reserva.hotelBookings || []).map((hotel) =>
    normalizeService(hotel, SERVICE_RECORD_KIND.HOTEL, {
      date: hotel.checkIn,
      name: firstNonEmpty(hotel.hotelName, hotel.description, "Hotel"),
    })
  );

  const transferBookings = (reserva.transferBookings || []).map((transfer) =>
    normalizeService(transfer, SERVICE_RECORD_KIND.TRANSFER, {
      date: transfer.pickupDateTime,
      name: firstNonEmpty(
        [transfer.pickupLocation, transfer.dropoffLocation].filter(Boolean).join(" > "),
        transfer.description,
        "Traslado"
      ),
    })
  );

  const packageBookings = (reserva.packageBookings || []).map((packageBooking) =>
    normalizeService(packageBooking, SERVICE_RECORD_KIND.PACKAGE, {
      date: packageBooking.startDate,
      name: firstNonEmpty(packageBooking.packageName, packageBooking.description, "Paquete"),
    })
  );

  const genericServices = (reserva.servicios || []).map((service) =>
    normalizeService(service, SERVICE_RECORD_KIND.GENERIC, {
      date: service.departureDate,
      name: firstNonEmpty(service.description, service.serviceType, service.productType, "Servicio"),
    })
  );

  return [
    ...flightSegments,
    ...hotelBookings,
    ...transferBookings,
    ...packageBookings,
    ...genericServices,
  ].sort((left, right) => toTimestamp(left.date) - toTimestamp(right.date));
}

export function getServiceCreateEndpoint(reservaId, serviceType) {
  const collection = SPECIFIC_CREATE_ENDPOINTS[serviceType];

  if (!collection) {
    return `/reservas/${reservaId}/services`;
  }

  return `/reservas/${reservaId}/${collection}`;
}

export function getRecordKindForServiceType(serviceType) {
  return SERVICE_TYPE_RECORD_KINDS[serviceType] || SERVICE_RECORD_KIND.GENERIC;
}

export function getServiceMutationEndpoint(reservaId, service) {
  const servicePublicId = getReservationServicePublicId(service);

  if (!servicePublicId) {
    throw new Error("No se encontro el identificador publico del servicio.");
  }

  if (service?.recordKind === SERVICE_RECORD_KIND.GENERIC) {
    return `/reservas/services/${servicePublicId}`;
  }

  const collection = RECORD_KIND_ENDPOINTS[service?.recordKind];

  if (!collection) {
    throw new Error("No se pudo determinar el tipo tecnico del servicio.");
  }

  return `/reservas/${reservaId}/${collection}/${servicePublicId}`;
}

export function findNormalizedService(reserva, targetService) {
  const targetPublicId = getReservationServicePublicId(targetService);
  const targetKind = targetService?.recordKind;

  return normalizeReservaServices(reserva).find((service) => {
    return (
      service.recordKind === targetKind &&
      getReservationServicePublicId(service) === targetPublicId
    );
  });
}
