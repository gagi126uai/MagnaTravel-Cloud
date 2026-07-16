import { test } from "node:test";
import assert from "node:assert/strict";
import {
  textoQueFaltaNotaCredito,
  fusionarComprobantesPorResolver,
} from "./comprobantesPorResolverLogic.js";

test("textoQueFaltaNotaCredito: RequiresManualReview tiene su propio texto", () => {
  assert.equal(textoQueFaltaNotaCredito("RequiresManualReview"), "Falta que alguien la revise");
});

test("textoQueFaltaNotaCredito: cualquier otro estado (incl. ManualReviewPending) cae al texto genérico", () => {
  assert.equal(textoQueFaltaNotaCredito("ManualReviewPending"), "Falta confirmar y emitir la devolución");
  assert.equal(textoQueFaltaNotaCredito(undefined), "Falta confirmar y emitir la devolución");
});

test("fusionarComprobantesPorResolver: junta multas y NC en una sola lista, multas primero", () => {
  const multas = [
    { bookingCancellationPublicId: "bc-1", reservaPublicId: "r-1", reservaNumero: "F-2026-1042", debitNoteStatus: "Failed", confirmedAt: "2026-07-01T00:00:00Z" },
  ];
  const notasCredito = [
    { bookingCancellationPublicId: "bc-2", reservaPublicId: "r-2", reservaNumero: "F-2026-1031", status: "ManualReviewPending", enteredReviewAt: "2026-07-08T00:00:00Z" },
  ];

  const resultado = fusionarComprobantesPorResolver(multas, notasCredito);

  assert.equal(resultado.length, 2);
  assert.equal(resultado[0].key, "multa-bc-1");
  assert.equal(resultado[0].comprobante, "Multa · cargo al cliente");
  assert.equal(resultado[0].reservaNumero, "F-2026-1042");
  assert.equal(resultado[0].queFalta, "El cobro de la multa no salió — hay que reintentar");

  assert.equal(resultado[1].key, "nc-bc-2");
  assert.equal(resultado[1].comprobante, "DEVOLUCIÓN · SERVICIO ANULADO");
  assert.equal(resultado[1].reservaNumero, "F-2026-1031");
  assert.equal(resultado[1].queFalta, "Falta confirmar y emitir la devolución");
});

test("fusionarComprobantesPorResolver: listas vacías o ausentes no rompen (defensivo)", () => {
  assert.deepEqual(fusionarComprobantesPorResolver([], []), []);
  assert.deepEqual(fusionarComprobantesPorResolver(null, undefined), []);
});

test("fusionarComprobantesPorResolver: reservaPublicId ausente cae a null (fila sin link roto)", () => {
  const multas = [{ bookingCancellationPublicId: "bc-1", reservaNumero: "F-2026-1042", debitNoteStatus: "Pending", confirmedAt: null }];
  const resultado = fusionarComprobantesPorResolver(multas, []);
  assert.equal(resultado[0].reservaPublicId, null);
});
