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
