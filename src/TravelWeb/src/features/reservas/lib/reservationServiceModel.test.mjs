import test from "node:test";
import assert from "node:assert/strict";

import {
  SERVICE_RECORD_KIND,
  getRecordKindForServiceType,
  getServiceMutationEndpoint,
  normalizeReservaServices,
} from "./reservationServiceModel.js";

test("generic service with Hotel visual type remains generic", () => {
  const [service] = normalizeReservaServices({
    servicios: [
      {
        publicId: "generic-hotel",
        serviceType: "Hotel",
        description: "Hotel cargado como servicio legacy",
        departureDate: "2026-05-10T00:00:00Z",
      },
    ],
  });

  assert.equal(service.recordKind, SERVICE_RECORD_KIND.GENERIC);
  assert.equal(service.displayType, "Hotel");
  assert.equal(getServiceMutationEndpoint("reserva-1", service), "/reservas/services/generic-hotel");
});

test("specific hotel booking normalizes as hotel", () => {
  const [service] = normalizeReservaServices({
    hotelBookings: [
      {
        publicId: "hotel-1",
        hotelName: "Hotel Central",
        checkIn: "2026-05-10T00:00:00Z",
      },
    ],
  });

  assert.equal(service.recordKind, SERVICE_RECORD_KIND.HOTEL);
  assert.equal(service.displayType, "Hotel");
  assert.equal(getServiceMutationEndpoint("reserva-1", service), "/reservas/reserva-1/hotels/hotel-1");
});

test("record kinds generate the correct mutation endpoints", () => {
  const services = normalizeReservaServices({
    flightSegments: [{ publicId: "flight-1", departureTime: "2026-05-01T00:00:00Z" }],
    hotelBookings: [{ publicId: "hotel-1", checkIn: "2026-05-02T00:00:00Z" }],
    transferBookings: [{ publicId: "transfer-1", pickupDateTime: "2026-05-03T00:00:00Z" }],
    packageBookings: [{ publicId: "package-1", startDate: "2026-05-04T00:00:00Z" }],
    servicios: [{ publicId: "generic-1", serviceType: "Paquete", departureDate: "2026-05-05T00:00:00Z" }],
  });

  const endpoints = Object.fromEntries(
    services.map((service) => [service.recordKind, getServiceMutationEndpoint("reserva-1", service)])
  );

  assert.deepEqual(endpoints, {
    [SERVICE_RECORD_KIND.FLIGHT]: "/reservas/reserva-1/flights/flight-1",
    [SERVICE_RECORD_KIND.HOTEL]: "/reservas/reserva-1/hotels/hotel-1",
    [SERVICE_RECORD_KIND.TRANSFER]: "/reservas/reserva-1/transfers/transfer-1",
    [SERVICE_RECORD_KIND.PACKAGE]: "/reservas/reserva-1/packages/package-1",
    [SERVICE_RECORD_KIND.GENERIC]: "/reservas/services/generic-1",
  });
});

test("visual service types map to technical record kinds for create validation", () => {
  assert.equal(getRecordKindForServiceType("Aereo"), SERVICE_RECORD_KIND.FLIGHT);
  assert.equal(getRecordKindForServiceType("Hotel"), SERVICE_RECORD_KIND.HOTEL);
  assert.equal(getRecordKindForServiceType("Traslado"), SERVICE_RECORD_KIND.TRANSFER);
  assert.equal(getRecordKindForServiceType("Paquete"), SERVICE_RECORD_KIND.PACKAGE);
  assert.equal(getRecordKindForServiceType("Otro"), SERVICE_RECORD_KIND.GENERIC);
});

// ─── Tests ADR-018: display de la fila prefiere campos de identidad ───────────

test("ADR-018: vuelo con productName → la fila muestra productName", () => {
  // ADR-018 §4-ter: la identidad visible del vuelo es productName (snapshot del buscador).
  const [service] = normalizeReservaServices({
    flightSegments: [
      {
        publicId: "flight-adr018",
        productName: "AEP–IGR LATAM",
        // campos estructurados null (creado desde la ficha inline)
        origin: null,
        destination: null,
        airlineCode: null,
        flightNumber: null,
        departureTime: "2026-08-12T00:00:00Z",
      },
    ],
  });
  assert.equal(service.name, "AEP–IGR LATAM");
});

test("ADR-018: vuelo legacy (productName null) → la fila sigue derivando del estructurado", () => {
  // Para vuelos creados antes de ADR-018, productName es null → fallback a origin/destination.
  const [service] = normalizeReservaServices({
    flightSegments: [
      {
        publicId: "flight-legacy",
        productName: null,
        origin: "AEP",
        destination: "IGR",
        airlineCode: "LA",
        flightNumber: "4101",
        departureTime: "2026-08-12T00:00:00Z",
      },
    ],
  });
  // El nombre derivado incluye la aerolínea/vuelo o la ruta — no debe ser vacío
  assert.ok(service.name.length > 0, "el nombre legacy no debe quedar vacío");
  assert.notEqual(service.name, "Vuelo", "no debe caer al default genérico cuando hay datos estructurados");
});

test("ADR-018: traslado con productName → la fila muestra productName", () => {
  const [service] = normalizeReservaServices({
    transferBookings: [
      {
        publicId: "transfer-adr018",
        productName: "EZE → Sheraton Tigre",
        pickupLocation: null,
        dropoffLocation: null,
        pickupDateTime: "2026-08-12T09:00:00Z",
      },
    ],
  });
  assert.equal(service.name, "EZE → Sheraton Tigre");
});

test("ADR-018: traslado legacy (productName null) → fallback a pickup/dropoff", () => {
  const [service] = normalizeReservaServices({
    transferBookings: [
      {
        publicId: "transfer-legacy",
        productName: null,
        pickupLocation: "EZE",
        dropoffLocation: "Hotel",
        pickupDateTime: "2026-08-12T09:00:00Z",
      },
    ],
  });
  assert.ok(service.name.includes("EZE"), "el nombre legacy debe incluir el pickup");
});

test("ADR-018: paquete con packageName → la fila muestra packageName", () => {
  const [service] = normalizeReservaServices({
    packageBookings: [
      {
        publicId: "package-adr018",
        packageName: "Iguazú 7 noches",
        destination: null,
        startDate: "2026-08-12T00:00:00Z",
      },
    ],
  });
  assert.equal(service.name, "Iguazú 7 noches");
});

test("ADR-018: asistencia con planType → la fila muestra planType", () => {
  const [service] = normalizeReservaServices({
    assistanceBookings: [
      {
        publicId: "assistance-adr018",
        planType: "AC 150 Americas Plata",
        supplierName: null,
        policyNumber: null,
        validFrom: "2026-08-12T00:00:00Z",
      },
    ],
  });
  assert.equal(service.name, "AC 150 Americas Plata");
});

test("ADR-018: asistencia legacy (planType null) → fallback a supplierName/policyNumber", () => {
  const [service] = normalizeReservaServices({
    assistanceBookings: [
      {
        publicId: "assistance-legacy",
        planType: null,
        supplierName: "Assist Card",
        policyNumber: "V-123456",
        validFrom: "2026-08-12T00:00:00Z",
      },
    ],
  });
  // Debe usar supplierName como fallback (segundo en la cadena)
  assert.equal(service.name, "Assist Card");
});
