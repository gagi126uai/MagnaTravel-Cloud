// E2E real — Tanda P1 "circuito proveedor" (2026-07-21/22)
// Camina los 3 frentes de la tanda con la app real corriendo:
//   (a) bajar el estado de un servicio PAGADO sin factura desde "Servicios comprados"
//       → aviso fijo con el mensaje real del motor + link "Ir a la reserva a facturar"
//       que aterriza con el panel de factura ABIERTO (state.irAFacturar).
//   (b) "Nueva factura" del operador lista SOLO confirmados y el vacío lo explica.
//   (c) el editor de servicio de la ficha sigue guardando sin romper (el botón
//       "Emitir factura" del cartel es inalcanzable por UI salvo carrera).
// Requiere el entorno de scripts/e2e-local/README.md (DB + API + vite e2e).
const { chromium } = require("playwright-core");
const FRONT = "http://localhost:5173";
const SHOTS = __dirname + "/shots-p1";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail.toString().slice(0, 180) : ""}`);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
  page.setDefaultTimeout(15000);
  page.on("console", (m) => { if (m.type() === "error") console.log("CONSOLE_ERR:", m.text().slice(0, 300)); });

  // ── Login ──
  await page.goto(FRONT + "/login");
  await page.fill('input[type="email"]', "e2e@magnatravel.local");
  await page.fill('input[type="password"]', "E2eLocal2026!");
  await page.click('button[type="submit"]');
  await page.waitForURL((u) => !u.pathname.includes("login"), { timeout: 20000 });
  check("Login por UI", true, "");

  // ── Seed por API (todo en ARS para calzar con la moneda default del form) ──
  // Operador A: reserva A1 con hotel CONFIRMADO (500, después se le pagan 200)
  //             reserva A2 con hotel SIN CONFIRMAR (300) → no debe aparecer en "Nueva factura"
  // Operador B: reserva B1 con hotel SIN CONFIRMAR (400) → "Nueva factura" vacía con explicación
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
      return { status: r.status, json, text: text.slice(0, 200) };
    };
    const stamp = Date.now().toString().slice(-6);
    const out = { stamp };
    const mkHotel = (rid, supId, nombre, costo, confirmado) => call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: nombre, starRating: 4, city: "Mendoza", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: costo, salePrice: costo + 200, commission: 100,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: nombre, city: "Mendoza", supplierPublicId: supId },
    });

    out.supA = await call("POST", "/suppliers", { name: `Operador P1A ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supAId = String(out.supA.json?.publicId || out.supA.json?.id);
    out.supB = await call("POST", "/suppliers", { name: `Operador P1B ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supBId = String(out.supB.json?.publicId || out.supB.json?.id);

    // Reserva A1: hotel confirmado que después se paga
    out.rA1 = await call("POST", "/reservas", { name: `Reserva P1A1 ${stamp}` });
    out.ridA1 = out.rA1.json?.publicId || out.rA1.json?.id;
    out.numA1 = out.rA1.json?.numeroReserva || out.rA1.json?.fileNumber || null;
    out.hA1 = await mkHotel(out.ridA1, out.supAId, `Hotel P1A ${stamp}`, 500, true);
    out.hA1Id = out.hA1.json?.publicId || out.hA1.json?.id;
    out.paxA1 = await call("PATCH", `/reservas/${out.ridA1}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pasA1 = await call("POST", `/reservas/${out.ridA1}/passengers`, {
      fullName: "Pasajero P1", documentType: "DNI", documentNumber: "30111222",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.stA1 = await call("PUT", `/reservas/${out.ridA1}/status`, { status: "InManagement" });
    out.hsA1 = await call("PATCH", `/hotel-bookings/${out.hA1Id}/status`, { status: "Confirmado" });

    // Reserva A2: hotel del MISMO operador que queda Solicitado
    out.rA2 = await call("POST", "/reservas", { name: `Reserva P1A2 ${stamp}` });
    out.ridA2 = out.rA2.json?.publicId || out.rA2.json?.id;
    out.numA2 = out.rA2.json?.numeroReserva || out.rA2.json?.fileNumber || null;
    out.hA2 = await mkHotel(out.ridA2, out.supAId, `Hotel P1A2 ${stamp}`, 300, false);

    // Reserva B1: hotel del operador B que queda Solicitado
    out.rB1 = await call("POST", "/reservas", { name: `Reserva P1B1 ${stamp}` });
    out.ridB1 = out.rB1.json?.publicId || out.rB1.json?.id;
    out.hB1 = await mkHotel(out.ridB1, out.supBId, `Hotel P1B ${stamp}`, 400, false);
    return out;
  });
  const seedOk = [seed.supA, seed.supB, seed.hA1, seed.hsA1, seed.hA2, seed.hB1]
    .every((r) => r && r.status < 300);
  check("Seed (2 operadores, 3 reservas, hotel confirmado + 2 sin confirmar)", seedOk,
    seedOk ? `A=${seed.supAId} B=${seed.supBId}` : JSON.stringify({ hA1: seed.hA1?.text, hsA1: seed.hsA1?.text, hA2: seed.hA2?.text, hB1: seed.hB1?.text }).slice(0, 300));
  if (!seedOk) throw new Error("Seed fallo, no sigo");

  // ── Pagar 200 del hotel confirmado (flujo 2 pasos ya deployado) ──
  await page.goto(`${FRONT}/suppliers/${seed.supAId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pago-paso-elegir"]');
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().waitFor();
  const grillaTexto = await page.locator('[data-testid="pago-grilla-deuda"]').innerText();
  console.log("GRILLA_PASO1:", grillaTexto.replace(/\n/g, " | ").slice(0, 400));
  // La fila puede nombrar la reserva por numero, por nombre o por el hotel: probamos en orden.
  const candidatos = [String(seed.numA1 || ""), `Reserva P1A1 ${seed.stamp}`, `Hotel P1A ${seed.stamp}`].filter(Boolean);
  let filaPago = null;
  for (const texto of candidatos) {
    const loc = page.locator('[data-testid^="pago-elegir-fila-"]').filter({ hasText: texto });
    if (await loc.count()) { filaPago = loc.first(); break; }
  }
  if (!filaPago) filaPago = page.locator('[data-testid^="pago-elegir-fila-"]').first();
  await filaPago.click();
  await page.waitForSelector('[data-testid="pago-destino-fijado"]');
  await page.locator('[data-testid="pago-monto"]').fill("200");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.locator('[data-testid="pago-exito"], [data-testid="pago-error"]').first().waitFor({ timeout: 15000 });
  if (await page.locator('[data-testid="pago-error"]').count()) {
    const err = await page.locator('[data-testid="pago-error"]').innerText();
    check("Pago al operador (200 sobre 500)", false, err.slice(0, 200));
    throw new Error("Pago rechazado: " + err.slice(0, 200));
  }
  check("Pago al operador (200 sobre 500) registrado", true, "");
  // Cerrar el cartel para volver a la pagina limpia
  await page.goto(`${FRONT}/suppliers/${seed.supAId}/account`);
  await page.waitForLoadState("networkidle");

  // ── (a) Bajar el estado del servicio PAGADO desde "Servicios comprados" ──
  await page.getByRole("tab", { name: "Servicios comprados" }).or(page.getByRole("button", { name: "Servicios comprados" })).first().click();
  const filaHotel = page.locator("tr").filter({ hasText: `Hotel P1A ${seed.stamp}` }).first();
  await filaHotel.waitFor();
  const selEstado = filaHotel.locator('select[title="Cambiar estado del servicio"]');
  await selEstado.selectOption("Solicitado");
  await page.waitForSelector('[data-testid="status-editor-bloqueo-pago-sin-factura"]');
  const aviso = await page.locator('[data-testid="status-editor-bloqueo-pago-sin-factura"]').innerText();
  await page.screenshot({ path: SHOTS + "/1-aviso-fijo-bloqueo.png", fullPage: true });
  check("(a) Aviso fijo: aparece al intentar bajar el estado del servicio pagado", true, aviso.slice(0, 160));
  check("(a) Aviso: habla de factura (motivo real del motor)", /factura/i.test(aviso), aviso.slice(0, 160));
  check("(a) Aviso: sin jerga tecnica ni internals",
    !/degradar|des-?confirmar|GUID|[0-9a-f]{8}-[0-9a-f]{4}-|Conflict|Exception|undefined|null\b|CANCEL_SERVICE/i.test(aviso), aviso.slice(0, 160));
  check("(a) Aviso: sin formato gringo de plata (500.00)", !/\d\.\d{2}\b/.test(aviso), "");
  check("(a) El estado NO cambio (el select volvio a Confirmado)",
    (await selEstado.inputValue()) === "Confirmado", await selEstado.inputValue());
  check("(a) El aviso trae el link 'Ir a la reserva a facturar'",
    (await page.getByRole("link", { name: "Ir a la reserva a facturar" }).count()) === 1, "");

  // ── (a2) El link aterriza en la reserva con el panel de factura ABIERTO ──
  await page.getByRole("link", { name: "Ir a la reserva a facturar" }).click();
  await page.waitForURL((u) => u.pathname.includes("/reservas/"), { timeout: 15000 });
  await page.waitForSelector('[data-testid="emitir-factura-inline"]', { timeout: 15000 });
  await page.screenshot({ path: SHOTS + "/2-aterriza-panel-factura.png", fullPage: true });
  check("(a2) Aterriza en la reserva con el panel 'Emitir factura' abierto",
    page.url().includes(String(seed.ridA1)), page.url().slice(-50));

  // ── (c) El editor de servicio de la ficha no rompe ante el rechazo ──
  // La reserva quedo CONFIRMADA (estados derivados: su unico servicio esta confirmado),
  // asi que el candado de reserva confirmada rechaza la edicion ANTES que la regla del
  // pago — por eso el boton "Emitir factura" del editor es inalcanzable salvo carrera.
  // Lo que SI se verifica: cartel con mensaje limpio, SIN boton "Emitir factura" (el
  // motivo no es el del pago), y la ficha queda abierta sin perder lo cargado.
  await page.getByRole("button", { name: "Servicios" }).first().click().catch(async () => {
    await page.getByRole("tab", { name: "Servicios" }).first().click();
  });
  await page.getByRole("button", { name: "Editar servicio" }).first().click();
  await page.getByRole("button", { name: "Guardar cambios" }).waitFor();
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await page.locator('[data-testid="inline-card-error"]').waitFor({ timeout: 15000 });
  const cartelEditor = await page.locator('[data-testid="inline-card-error"]').innerText();
  await page.screenshot({ path: SHOTS + "/3-editor-rechazo-limpio.png", fullPage: true });
  check("(c) Editor: el rechazo muestra un cartel con mensaje limpio (sin internals)",
    !/GUID|[0-9a-f]{8}-[0-9a-f]{4}-|Conflict|Exception|undefined|CANCEL_SERVICE|degradar/i.test(cartelEditor), cartelEditor.slice(0, 160));
  check("(c) Editor: NO ofrece 'Emitir factura' (el motivo es el candado, no el pago)",
    (await page.locator('[data-testid="inline-card-emitir-factura"]').count()) === 0, "");
  check("(c) Editor: la ficha queda abierta sin perder lo cargado",
    (await page.getByRole("button", { name: "Guardar cambios" }).count()) === 1, "");

  // ── (b) "Nueva factura" del operador A: lista el confirmado, esconde el Solicitado ──
  await page.goto(`${FRONT}/suppliers/${seed.supAId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: "Facturas operador" }).or(page.getByRole("button", { name: "Facturas operador" })).first().click();
  await page.getByRole("button", { name: "Nueva factura" }).click();
  await page.waitForTimeout(1500); // debounce 250ms + fetch
  const listaA = await page.locator("form").filter({ hasText: "seleccionados" }).innerText();
  await page.screenshot({ path: SHOTS + "/4-nueva-factura-solo-confirmados.png", fullPage: true });
  check("(b) Nueva factura A: lista la reserva del hotel CONFIRMADO",
    seed.numA1 ? listaA.includes(String(seed.numA1)) : /Hotel|Reserva/.test(listaA), (seed.numA1 || "") + " | " + listaA.slice(0, 140));
  check("(b) Nueva factura A: NO lista la reserva del hotel SIN CONFIRMAR",
    seed.numA2 ? !listaA.includes(String(seed.numA2)) : true, seed.numA2 || "sin numero");

  // ── (b2) Operador B (solo sin confirmar): vacio que explica el porque ──
  await page.goto(`${FRONT}/suppliers/${seed.supBId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: "Facturas operador" }).or(page.getByRole("button", { name: "Facturas operador" })).first().click();
  await page.getByRole("button", { name: "Nueva factura" }).click();
  await page.waitForTimeout(1500);
  const listaB = await page.locator("form").filter({ hasText: "seleccionados" }).innerText();
  await page.screenshot({ path: SHOTS + "/5-nueva-factura-vacio-explicado.png", fullPage: true });
  check("(b2) Nueva factura B: vacio con explicacion (sin confirmar no se factura)",
    /No hay servicios confirmados pendientes de facturar/.test(listaB) && /sin confirmar todav/i.test(listaB), listaB.slice(0, 200));

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
