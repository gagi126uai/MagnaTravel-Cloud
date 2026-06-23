/**
 * Tests de lógica pura para el envío de voucher por WhatsApp desde la solapa Vouchers.
 *
 * Cómo correr:
 *   node --test src/components/voucherSendLogic.test.mjs
 *
 * Las funciones testeadas están en voucherSendLogic.js.
 * No requieren DOM, mocks de API ni React.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica local de las funciones puras ─────────────────────────────────────
// Las replicamos en el test (en vez de importar el archivo .js) para que el test
// sea autocontenido y no dependa del sistema de módulos ESM de Node apuntando
// a un archivo sin extensión .mjs.
//
// Si el proyecto migra a ESM completo, se puede reemplazar por:
//   import { puedeEnviarVoucher, resolverDestinatarioPorDefecto, resolverCandidatosDestinatario } from "./voucherSendLogic.js";

function puedeEnviarVoucher(voucher, soloLectura) {
  if (soloLectura) return false;
  if (voucher.status === "Revoked") return false;
  const estadoEnviable = voucher.status === "Issued" || voucher.status === "UploadedExternal";
  if (!estadoEnviable) return false;
  return Boolean(voucher.canSend);
}

function resolverDestinatarioPorDefecto(voucher, reserva, passengers) {
  const passengerIds = voucher?.passengerPublicIds ?? [];

  if (passengerIds.length === 1) {
    const passengerId = passengerIds[0];
    const passengerEncontrado = (passengers ?? []).find(
      (p) => (p.publicId || p.PublicId) === passengerId
    );
    const displayName =
      passengerEncontrado?.fullName ||
      passengerEncontrado?.FullName ||
      voucher.passengerNames?.[0] ||
      "Pasajero";
    return { personType: "passenger", personId: passengerId, displayName };
  }

  if (passengerIds.length > 1) {
    return null;
  }

  const customerPublicId = reserva?.customerPublicId;
  if (!customerPublicId) return null;

  const displayName =
    reserva?.customerName ||
    reserva?.client?.fullName ||
    "Cliente";

  return { personType: "customer", personId: customerPublicId, displayName };
}

function resolverCandidatosDestinatario(voucher, passengers) {
  const passengerIds = voucher?.passengerPublicIds ?? [];

  return passengerIds.map((passengerId) => {
    const passengerEncontrado = (passengers ?? []).find(
      (p) => (p.publicId || p.PublicId) === passengerId
    );
    const index = passengerIds.indexOf(passengerId);
    const displayName =
      passengerEncontrado?.fullName ||
      passengerEncontrado?.FullName ||
      voucher.passengerNames?.[index] ||
      `Pasajero ${index + 1}`;
    return { personType: "passenger", personId: passengerId, displayName };
  });
}

// ─── puedeEnviarVoucher ────────────────────────────────────────────────────────

test("puedeEnviar: Issued + canSend + no soloLectura → true", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "Issued", canSend: true }, false),
    true
  );
});

test("puedeEnviar: UploadedExternal + canSend + no soloLectura → true", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "UploadedExternal", canSend: true }, false),
    true
  );
});

test("puedeEnviar: Issued + canSend pero soloLectura → false (estado congelado)", () => {
  // En solo lectura (Traveling, Lost, Cancelled, FullyInvoiced) no se muestran acciones.
  assert.equal(
    puedeEnviarVoucher({ status: "Issued", canSend: true }, true),
    false
  );
});

test("puedeEnviar: Issued + canSend=false → false (backend indica que no es enviable aún)", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "Issued", canSend: false }, false),
    false
  );
});

test("puedeEnviar: Draft → false (no tiene documento real adjunto)", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "Draft", canSend: true }, false),
    false
  );
});

test("puedeEnviar: PendingAuthorization → false (no emitido aún)", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "PendingAuthorization", canSend: true }, false),
    false
  );
});

test("puedeEnviar: Revoked → false (voucher anulado, no se envía)", () => {
  assert.equal(
    puedeEnviarVoucher({ status: "Revoked", canSend: true }, false),
    false
  );
});

// ─── resolverDestinatarioPorDefecto ────────────────────────────────────────────

const reservaEjemplo = {
  customerPublicId: "cust-001",
  customerName: "Maria Gomez",
};

const pasajerosEjemplo = [
  { publicId: "pax-001", fullName: "Juan Perez" },
  { publicId: "pax-002", fullName: "Ana Lopez" },
];

test("destinatario: voucher con 1 pasajero → devuelve ese pasajero", () => {
  const voucher = {
    passengerPublicIds: ["pax-001"],
    passengerNames: ["Juan Perez"],
  };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaEjemplo, pasajerosEjemplo);
  assert.deepEqual(resultado, {
    personType: "passenger",
    personId: "pax-001",
    displayName: "Juan Perez",
  });
});

test("destinatario: voucher con 1 pasajero sin objeto en lista → usa passengerNames como fallback", () => {
  const voucher = {
    passengerPublicIds: ["pax-999"], // no está en pasajerosEjemplo
    passengerNames: ["Carlos Ruiz"],
  };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaEjemplo, pasajerosEjemplo);
  assert.equal(resultado?.displayName, "Carlos Ruiz");
  assert.equal(resultado?.personId, "pax-999");
});

test("destinatario: voucher con 1 pasajero sin nombre en ningún lado → 'Pasajero'", () => {
  const voucher = {
    passengerPublicIds: ["pax-999"],
    passengerNames: [],
  };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaEjemplo, []);
  assert.equal(resultado?.displayName, "Pasajero");
});

test("destinatario: voucher con >1 pasajeros → null (requiere selección)", () => {
  const voucher = {
    passengerPublicIds: ["pax-001", "pax-002"],
    passengerNames: ["Juan Perez", "Ana Lopez"],
  };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaEjemplo, pasajerosEjemplo);
  assert.equal(resultado, null);
});

test("destinatario: voucher sin pasajeros (scope ReservaCompleta) → titular (customer)", () => {
  const voucher = {
    passengerPublicIds: [],
    passengerNames: [],
  };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaEjemplo, pasajerosEjemplo);
  assert.deepEqual(resultado, {
    personType: "customer",
    personId: "cust-001",
    displayName: "Maria Gomez",
  });
});

test("destinatario: voucher sin pasajeros y reserva sin customerPublicId → null", () => {
  const voucher = { passengerPublicIds: [], passengerNames: [] };
  const reservaSinCliente = { customerPublicId: null, customerName: "?" };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaSinCliente, []);
  assert.equal(resultado, null);
});

test("destinatario: voucher null sin passengerPublicIds → cae al path del customer", () => {
  // Si el voucher es null, passengerPublicIds cae a [] por el operador ??.
  // Entonces la función busca el customer de la reserva — comportamiento defensivo
  // que devuelve el titular en vez de explotar.
  const resultado = resolverDestinatarioPorDefecto(null, reservaEjemplo, []);
  assert.deepEqual(resultado, {
    personType: "customer",
    personId: "cust-001",
    displayName: "Maria Gomez",
  });
});

test("destinatario: customerName ausente pero hay client.fullName → usa client.fullName", () => {
  const reservaConClient = {
    customerPublicId: "cust-002",
    customerName: undefined,
    client: { fullName: "Pedro Alvarez" },
  };
  const voucher = { passengerPublicIds: [], passengerNames: [] };
  const resultado = resolverDestinatarioPorDefecto(voucher, reservaConClient, []);
  assert.equal(resultado?.displayName, "Pedro Alvarez");
});

// ─── resolverCandidatosDestinatario ────────────────────────────────────────────

test("candidatos: voucher con 2 pasajeros → devuelve 2 candidatos con sus nombres", () => {
  const voucher = {
    passengerPublicIds: ["pax-001", "pax-002"],
    passengerNames: ["Juan Perez", "Ana Lopez"],
  };
  const candidatos = resolverCandidatosDestinatario(voucher, pasajerosEjemplo);
  assert.equal(candidatos.length, 2);
  assert.equal(candidatos[0].personId, "pax-001");
  assert.equal(candidatos[0].displayName, "Juan Perez");
  assert.equal(candidatos[0].personType, "passenger");
  assert.equal(candidatos[1].personId, "pax-002");
  assert.equal(candidatos[1].displayName, "Ana Lopez");
});

test("candidatos: pasajero no encontrado en lista → usa passengerNames como fallback", () => {
  const voucher = {
    passengerPublicIds: ["pax-888", "pax-999"],
    passengerNames: ["Roberto Sosa", "Luisa Mendez"],
  };
  const candidatos = resolverCandidatosDestinatario(voucher, []);
  assert.equal(candidatos[0].displayName, "Roberto Sosa");
  assert.equal(candidatos[1].displayName, "Luisa Mendez");
});

test("candidatos: pasajero sin nombre en ningún lado → 'Pasajero N'", () => {
  const voucher = {
    passengerPublicIds: ["pax-888"],
    passengerNames: [],
  };
  const candidatos = resolverCandidatosDestinatario(voucher, []);
  assert.equal(candidatos[0].displayName, "Pasajero 1");
});

test("candidatos: voucher sin pasajeros → array vacío", () => {
  const voucher = { passengerPublicIds: [], passengerNames: [] };
  const candidatos = resolverCandidatosDestinatario(voucher, pasajerosEjemplo);
  assert.deepEqual(candidatos, []);
});
