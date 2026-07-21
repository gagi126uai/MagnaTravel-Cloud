// E2E real Tandas 2-9 — contrato pantalla-motor (2026-07-20)
// Cubre lo verificable con datos sembrados en la base local:
//   T5 (emitir factura apagada por estado, sin enums), T6 (candado por cobro),
//   T7 (papelera avisa antes, motivo real), T4 (vocabulario "anular" en el motivo),
//   T3 (code en el 409 de anular reserva), T2 (regresion: anular comprobante anda).
// NO cubre (queda dicho): T2 modal de autorizacion (necesita usuario sin permiso),
//   T8 (ND con tributos), T9 (fallo de PDF) — cubiertos por tests unit/integracion.
const { chromium } = require("playwright-core");
const FRONT = "http://localhost:5173";
const SHOTS = __dirname + "/shots-t2a9";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail.toString().slice(0, 160) : ""}`);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
  page.setDefaultTimeout(15000);

  // ── Login por la UI real ──
  await page.goto(FRONT + "/login");
  await page.fill('input[type="email"]', "e2e@magnatravel.local");
  await page.fill('input[type="password"]', "E2eLocal2026!");
  await page.click('button[type="submit"]');
  await page.waitForURL((u) => !u.pathname.includes("login"), { timeout: 20000 });
  check("Login por UI", true, page.url());

  // ── Sembrado same-origin (mismas cookies/CSRF que la app) ──
  const seed = await page.evaluate(async () => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const call = async (method, path, body) => {
      const r = await fetch("/api" + path, {
        method, credentials: "include",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
        body: body ? JSON.stringify(body) : undefined,
      });
      const text = await r.text();
      let json = null; try { json = JSON.parse(text); } catch {}
      return { status: r.status, json, text: text.slice(0, 300) };
    };
    const out = {};
    const stamp = Date.now().toString().slice(-6);
    out.stamp = stamp;
    out.sup = await call("POST", "/suppliers", { name: `Operador T7 ${stamp}`, defaultCurrency: "USD", invoicingMode: 0 });
    const supId = String(out.sup.json?.publicId || out.sup.json?.id);
    out.supId = supId;
    // Reserva principal (T6+T7): hotel USD confirmado, En gestion
    out.reserva = await call("POST", "/reservas", { name: `Reserva E2E TX ${stamp}` });
    const rid = out.reserva.json?.publicId || out.reserva.json?.id;
    out.rid = rid;
    out.hotel = await call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: `Hotel TX ${stamp}`, starRating: 4, city: "Salta", country: "AR",
      checkIn: "2026-11-10T00:00:00Z", checkOut: "2026-11-15T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: 500, salePrice: 700, commission: 200,
      notes: null, currency: "USD",
      newCatalogProduct: { name: `Hotel TX ${stamp}`, city: "Salta", supplierPublicId: supId },
    });
    out.hid = out.hotel.json?.publicId || out.hotel.json?.id;
    out.pax = await call("PATCH", `/reservas/${rid}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pas = await call("POST", `/reservas/${rid}/passengers`, {
      fullName: "Pasajero TX", documentType: "DNI", documentNumber: "30999888",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.st = await call("PUT", `/reservas/${rid}/status`, { status: "InManagement" });
    out.hs = await call("PATCH", `/hotel-bookings/${out.hid}/status`, { status: "Confirmado" });
    // Cobro del cliente + recibo emitido (T6)
    out.pay = await call("POST", "/payments", {
      reservaId: rid, amount: 100, currency: "USD", method: "Transferencia",
      paidAt: new Date().toISOString(), notes: "Cobro E2E T6",
    });
    const pid = out.pay.json?.publicId || out.pay.json?.id;
    out.pid = pid;
    out.receipt = await call("POST", `/payments/${pid}/receipt`);
    // Reserva en Presupuesto (T5 + T3)
    out.reservaP = await call("POST", "/reservas", { name: `Reserva E2E Presupuesto ${stamp}` });
    out.ridP = out.reservaP.json?.publicId || out.reservaP.json?.id;
    return out;
  });
  console.log("SEED:", JSON.stringify({ sup: seed.sup.status, reserva: seed.reserva.status, hotel: seed.hotel.status, st: seed.st.status, hs: seed.hs.status, pay: [seed.pay.status, seed.pay.text.slice(0, 80)], receipt: seed.receipt.status, reservaP: seed.reservaP.status }));
  check("Seed (operador + reserva + hotel confirmado + cobro con recibo + presupuesto)",
    seed.sup.status < 300 && seed.hotel.status < 300 && seed.hs.status < 300 && seed.pay.status < 300 && seed.receipt.status < 300 && seed.reservaP.status < 300, `R=${seed.rid} P=${seed.ridP}`);

  // ── T7 sembrado: pagarle 200 USD al operador imputado al hotel (via UI, flujo T1 probado) ──
  await page.goto(`${FRONT}/suppliers/${seed.supId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pagar-proveedor-inline"]');
  const imputar = page.locator('[data-testid="pago-imputar-a"]');
  if (await imputar.count()) {
    const tagName = await imputar.evaluate((el) => el.tagName);
    if (tagName === "SELECT") {
      const opts = await imputar.locator("option").allTextContents();
      await imputar.selectOption({ index: opts.findIndex((o) => /reserva/i.test(o)) });
    }
  }
  await page.waitForTimeout(400);
  const selReserva = page.locator('[data-testid="pago-reserva"]');
  await selReserva.waitFor();
  let ops = await selReserva.locator("option").allTextContents();
  let i = ops.findIndex((o) => o.includes(`Reserva E2E TX ${seed.stamp}`));
  await selReserva.selectOption({ index: i >= 0 ? i : 1 });
  await page.waitForTimeout(1500);
  const servSel = page.locator("select", { has: page.locator("option", { hasText: `Hotel TX ${seed.stamp}` }) }).first();
  if (await servSel.count()) {
    const so = await servSel.locator("option").allTextContents();
    await servSel.selectOption({ index: so.findIndex((o) => o.includes(`Hotel TX ${seed.stamp}`)) });
  }
  await page.locator('[data-testid="pago-monto"]').fill("200");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.waitForTimeout(2500);
  const bodyPago = await page.locator("body").innerText();
  check("T7 seed: pago USD 200 al operador registrado", /Pago registrado/i.test(bodyPago), "");

  // ── DTO de la reserva: los candados nuevos viajan y dicen la verdad ──
  const dto = await page.evaluate(async (rid) => {
    const r = await fetch(`/api/reservas/${rid}`, { credentials: "include" });
    return await r.json();
  }, seed.rid);
  const hotelDto = (dto.hotelBookings || [])[0] || {};
  const payDto = (dto.payments || [])[0] || {};
  check("T7: canCancel del hotel BLOQUEADO por freno R1 (pago al operador sin factura)",
    hotelDto.canCancel && hotelDto.canCancel.allowed === false && /pagos al operador/i.test(hotelDto.canCancel.reason || ""),
    JSON.stringify(hotelDto.canCancel));
  check("T4: el motivo dice 'anular' (vocabulario del dueno), sin codigos tecnicos",
    /anular/i.test(hotelDto.canCancel?.reason || "") && !/CANCEL_SERVICE|Exception|Parameter/i.test(hotelDto.canCancel?.reason || ""),
    hotelDto.canCancel?.reason);
  check("T6: canEdit del cobro BLOQUEADO por recibo emitido",
    payDto.canEdit && payDto.canEdit.allowed === false && /recibo/i.test(payDto.canEdit.reason || ""),
    JSON.stringify(payDto.canEdit));
  check("T6: canDelete del cobro BLOQUEADO por comprobante vigente",
    payDto.canDelete && payDto.canDelete.allowed === false, JSON.stringify(payDto.canDelete));

  // ── Ficha real: papelera gris con motivo a la vista (T7) + candado del cobro (T6) ──
  await page.goto(`${FRONT}/reservas/${seed.rid}`);
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(1500);
  const aviso = page.locator(`[data-testid^="aviso-bloqueo-anular-"]`).first();
  const avisoVisible = await aviso.count() > 0 && await aviso.first().isVisible().catch(() => false);
  const avisoTexto = avisoVisible ? await aviso.first().innerText() : "";
  check("T7 UI: chip con candado y motivo VISIBLE al lado de la papelera", avisoVisible, avisoTexto);
  const papelera = page.locator(`[data-testid^="btn-delete-service-"]:not([data-testid*="mobile"])`).first();
  const papeleraDeshabilitada = await papelera.count() > 0 ? await papelera.isDisabled().catch(() => false) : false;
  check("T7 UI: papelera deshabilitada de verdad", papeleraDeshabilitada, "");
  await page.screenshot({ path: SHOTS + "/1-t7-papelera-bloqueada.png", fullPage: true });

  // El extracto (y los botones del cobro) viven en la solapa "Estado de Cuenta".
  await page.getByRole("button", { name: /Estado de Cuenta/i }).first().click()
    .catch(() => page.getByText("Estado de Cuenta").first().click());
  await page.waitForTimeout(1500);
  const bodyFicha = await page.locator("body").innerText();
  check("T6 UI: el motivo del candado del cobro esta a la vista en el extracto",
    /No se puede editar el pago porque tiene un recibo emitido/i.test(bodyFicha), "");
  const btnEditarCobro = page.locator('[data-testid="btn-editar-cobro"]').first();
  const editarDeshab = await btnEditarCobro.count() > 0 ? await btnEditarCobro.isDisabled().catch(() => false) : null;
  check("T6 UI: boton Editar del cobro deshabilitado", editarDeshab === true, `count=${await btnEditarCobro.count()}`);
  await page.screenshot({ path: SHOTS + "/2-t6-cobro-candado.png", fullPage: true });

  // ── T5: reserva en Presupuesto — capacidad de facturar apagada, motivo limpio ──
  const dtoP = await page.evaluate(async (rid) => {
    const r = await fetch(`/api/reservas/${rid}`, { credentials: "include" });
    return await r.json();
  }, seed.ridP);
  const capFact = dtoP.capabilities?.canInvoiceSale || {};
  check("T5: canInvoiceSale apagada en Presupuesto", capFact.allowed === false, JSON.stringify(capFact));
  check("T5: el motivo NO contiene nombres de estado internos (InManagement/Quotation/...)",
    !/(InManagement|Quotation|Budget|Confirmed|Cancelled|Traveling)/.test(capFact.reason || ""), capFact.reason);

  // ── T3: anular con saldo a favor una reserva no firme → 409 con code + mensaje criollo ──
  const t3 = await page.evaluate(async (rid) => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const r = await fetch(`/api/reservas/${rid}/annul-with-credit`, {
      method: "POST", credentials: "include",
      headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
      body: JSON.stringify({ reason: "Prueba E2E de anulacion con saldo a favor" }),
    });
    const text = await r.text();
    let json = null; try { json = JSON.parse(text); } catch {}
    return { status: r.status, json, text: text.slice(0, 300) };
  }, seed.ridP);
  check("T3: 409 con code ANNUL_CREDIT_NOT_FIRM_STATE",
    t3.status === 409 && t3.json?.code === "ANNUL_CREDIT_NOT_FIRM_STATE", JSON.stringify(t3));
  check("T3: el message sigue criollo y sin internals",
    typeof t3.json?.message === "string" && t3.json.message.length > 10 && !/Exception|Parameter|Npgsql/i.test(t3.json.message), t3.json?.message);

  // ── T2 (regresion): anular el comprobante desde la ficha anda (Admin con permiso) ──
  const t2 = await page.evaluate(async (pid) => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const r = await fetch(`/api/payments/${pid}/receipt/void`, {
      method: "POST", credentials: "include",
      headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
      body: JSON.stringify({ reason: null }),
    });
    const text = await r.text();
    return { status: r.status, text: text.slice(0, 200) };
  }, seed.pid);
  check("T2 regresion: anular comprobante con permiso sigue andando (200)", t2.status === 200, t2.text);

  // ── T6 de vuelta: con recibo ANULADO, editar sigue bloqueado POR AUDITORIA
  // (asimetria intencional del motor, preservada por la politica) ──
  const dto2 = await page.evaluate(async (rid) => {
    const r = await fetch(`/api/reservas/${rid}`, { credentials: "include" });
    return await r.json();
  }, seed.rid);
  const payDto2 = (dto2.payments || [])[0] || {};
  check("T6: con recibo anulado, canEdit bloquea por auditoria (regla real del motor)",
    payDto2.canEdit && payDto2.canEdit.allowed === false && /auditor/i.test(payDto2.canEdit.reason || ""),
    JSON.stringify(payDto2.canEdit));

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
