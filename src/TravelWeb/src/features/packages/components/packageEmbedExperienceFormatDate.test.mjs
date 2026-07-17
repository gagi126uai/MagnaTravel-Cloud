/**
 * Tests de formatDate() de PackageEmbedExperience — bug "fechas corridas un día"
 * (dueño, 2026-07-16). Sumado al alcance porque esta es la página PÚBLICA de venta
 * de paquetes (embed): un día corrido acá lo ve directamente el cliente final, antes
 * de comprar.
 *
 * departure.startDate es una fecha-solo-día (día de salida del paquete, sin hora)
 * que el backend guarda como medianoche UTC. Antes se pasaba directo por
 * new Date(value).toLocaleDateString(), que la corría un día hacia atrás en
 * Argentina (UTC-3).
 *
 * PackageEmbedExperience.jsx tiene JSX (no se puede importar directo en
 * node --test), así que copiamos la función — mismo patrón que el resto de los
 * tests de este proyecto (ver reprogramarViajeModal.test.mjs).
 *
 * A diferencia de la formatDate() central de utils.js (que muestra "23/05/2026"),
 * esta página pública usa formato largo ("23 de mayo de 2026") — se preserva ese
 * estilo visual, solo se corrige el día.
 *
 * Cómo correr: node --test src/features/packages/components/packageEmbedExperienceFormatDate.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de PackageEmbedExperience.jsx ────────────────────────

const FECHA_SOLO_DIA_REGEX = /^(\d{4})-(\d{2})-(\d{2})(?:T00:00:00(?:\.\d+)?Z?)?$/;

function formatDate(value) {
  if (!value) {
    return "-";
  }

  if (typeof value === "string") {
    const match = FECHA_SOLO_DIA_REGEX.exec(value);
    if (match) {
      const [, anio, mes, dia] = match;
      const fechaLocal = new Date(Number(anio), Number(mes) - 1, Number(dia));
      return fechaLocal.toLocaleDateString("es-AR", {
        day: "numeric",
        month: "long",
        year: "numeric",
      });
    }
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "-"
    : date.toLocaleDateString("es-AR", {
        day: "numeric",
        month: "long",
        year: "numeric",
      });
}

// ─── Tests: fecha-solo-día (caso real: departure.startDate) ─────────────────

test("formatDate: fecha-solo-día 'YYYY-MM-DD' → mismo día, formato largo", () => {
    assert.equal(formatDate("2026-05-23"), "23 de mayo de 2026");
});

test("formatDate: medianoche UTC con 'Z' → mismo día calendario del string", () => {
    assert.equal(formatDate("2026-05-23T00:00:00Z"), "23 de mayo de 2026");
});

test("formatDate: medianoche UTC con milisegundos '.000Z' → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00.000Z"), "23 de mayo de 2026");
});

// ─── Tests: instante real con hora — sin cambios ─────────────────────────────

test("formatDate: timestamp real con hora → se sigue mostrando en hora local", () => {
    const esperado = new Date("2026-05-23T14:30:00Z").toLocaleDateString("es-AR", {
        day: "numeric", month: "long", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T14:30:00Z"), esperado);
});

// ─── Tests: casos vacíos / inválidos ─────────────────────────────────────────

test("formatDate: null/undefined/cadena vacía → '-'", () => {
    assert.equal(formatDate(null), "-");
    assert.equal(formatDate(undefined), "-");
    assert.equal(formatDate(""), "-");
});

test("formatDate: valor no parseable → '-'", () => {
    assert.equal(formatDate("no-es-una-fecha-real"), "-");
});

// ─── Tests: casos borde de calendario ────────────────────────────────────────

test("formatDate: fin de mes (31 de mayo) → no salta a junio", () => {
    assert.equal(formatDate("2026-05-31T00:00:00Z"), "31 de mayo de 2026");
});

test("formatDate: fin de año (31 de diciembre) → no salta al año siguiente", () => {
    assert.equal(formatDate("2026-12-31T00:00:00Z"), "31 de diciembre de 2026");
});

test("formatDate: 1 de enero → no retrocede al 31 de diciembre del año anterior", () => {
    // Caso exacto del bug original: en UTC-3 el 1/1 00:00 UTC cae el 31/12 a las 21:00 local.
    assert.equal(formatDate("2026-01-01T00:00:00Z"), "1 de enero de 2026");
});

test("formatDate: 29 de febrero en año bisiesto → se muestra correctamente", () => {
    assert.equal(formatDate("2028-02-29T00:00:00Z"), "29 de febrero de 2028");
});

// ─── Ida y vuelta — caso real de la fecha de salida del paquete ─────────────

test("formatDate: ida y vuelta — el día de salida del paquete se muestra sin corrimiento", () => {
    const diaDeSalidaDelPaquete = "2026-05-23";
    const respuestaSimuladaDelBackend = `${diaDeSalidaDelPaquete}T00:00:00Z`;
    assert.equal(formatDate(respuestaSimuladaDelBackend), "23 de mayo de 2026");
});
