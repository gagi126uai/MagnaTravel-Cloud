// E2E real — Tanda P3 "circuito proveedor": cartel ámbar de confirmación al bajar el
// costo de un servicio por debajo de lo que ya se le pagó al operador (2026-07-22).
//
// Camina con la app real corriendo:
//   1. Paga 200 al operador imputados AL SERVICIO hotel1 (no a la reserva en general).
//   2. Edita el costo de hotel1 a 25/noche (total $100, por debajo de los $200 pagados).
//   3. El motor NO bloquea pero exige confirmar (409 COST_BELOW_PAID_CONFIRMATION_REQUIRED):
//      cartel ÁMBAR con el mensaje real del motor, "Volver a corregir" y "Sí, confirmar".
//   4. "Volver a corregir": el cartel se va, la ficha sigue abierta con lo cargado.
//   5. Reintentar guardar + "Sí, confirmar": guarda en silencio, la ficha se cierra.
//   6. Sincronización: el reconciliador de saldo a favor del operador corrió EN EL MOMENTO
//      de la edición (sin esperar otro pago/anulación) — el chip "Saldo a favor" ya muestra
//      los $100 sobrantes.
//
// TRAMPA CONOCIDA: si la reserva queda con UN SOLO servicio y ese se confirma, los estados
// derivados la pasan a "Confirmada" y el candado de reserva confirmada (ADR-020 F4) rechaza
// CUALQUIER edición ANTES de llegar al guard nuevo de esta tanda. Por eso el seed carga DOS
// servicios del mismo operador: hotel1 (el que se paga y edita, queda Confirmado) y hotel2
// (queda Solicitado a propósito), así la reserva se queda en "InManagement".
//
// Requiere el entorno de scripts/e2e-local/README.md (DB + API + vite e2e).

const { chromium } = require("playwright-core");
const http = require("http");

const FRONT = "http://localhost:5173";
const API = "http://localhost:59663";
const SHOTS = __dirname + "/shots-p3";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail.toString().slice(0, 220) : ""}`);
}

// ── Helper: espera en loop a que la API responda algo (arriba compilando ahora mismo) ──
// Cualquier respuesta HTTP (incluso 404) prueba que Kestrel ya está escuchando y ruteando;
// mientras compila, la conexión se rechaza y el helper reintenta.
function esperarApi(timeoutMs = 150000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const intento = () => {
      const req = http.get(API + "/health", (res) => {
        res.resume();
        resolve(true);
      });
      req.on("error", () => {
        if (Date.now() - start > timeoutMs) return reject(new Error("La API no respondio a tiempo"));
        setTimeout(intento, 2000);
      });
      req.setTimeout(3000, () => req.destroy());
    };
    intento();
  });
}

// Textos que NUNCA deberían aparecer en algo que ve el vendedor: jerga interna, GUIDs,
// nombres de excepciones .NET, formato de plata "gringo" (punto como decimal).
const JERGA_INTERNA = /allocation|Reassociate|NetCost|Exception|COST_BELOW|ServiceRecordKind|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-/i;
const FORMATO_GRINGO = /\$\s?\d[\d,]*\.\d{2}\b/; // "$200.00" en vez de "$200,00"
const textosCapturados = [];

(async () => {
  console.log("Esperando a que la API responda (puede tardar 1-2 min compilando)...");
  await esperarApi();
  console.log("API arriba.");

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

  // ── Seed por API: operador P3 + UNA reserva con DOS hoteles (hotel1 confirmado, hotel2
  // Solicitado a propósito para que la reserva NO quede "Confirmada" — ver TRAMPA arriba) ──
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

    const mkHotel = (rid, supId, nombre, costo) => call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: nombre, starRating: 4, city: "Bariloche", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z", // 4 noches
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: costo, salePrice: costo + 200, commission: 100,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: nombre, city: "Bariloche", supplierPublicId: supId },
    });

    out.sup = await call("POST", "/suppliers", { name: `Operador P3 ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supId = String(out.sup.json?.publicId || out.sup.json?.id);

    out.r = await call("POST", "/reservas", { name: `Reserva P3 ${stamp}` });
    out.rid = out.r.json?.publicId || out.r.json?.id;
    out.numReserva = out.r.json?.numeroReserva || out.r.json?.fileNumber || null;

    // hotel1: el que se paga y edita. netCost 500 → se le pagan 200 → se edita a 25/noche (=100).
    out.h1 = await mkHotel(out.rid, out.supId, `Hotel P3 Uno ${stamp}`, 500);
    out.h1Id = out.h1.json?.publicId || out.h1.json?.id;

    // hotel2: queda Solicitado a propósito, solo para que la reserva NO se auto-confirme.
    out.h2 = await mkHotel(out.rid, out.supId, `Hotel P3 Dos ${stamp}`, 300);
    out.h2Id = out.h2.json?.publicId || out.h2.json?.id;

    out.pax = await call("PATCH", `/reservas/${out.rid}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pasajero = await call("POST", `/reservas/${out.rid}/passengers`, {
      fullName: "Pasajero P3", documentType: "DNI", documentNumber: "30333444",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });

    out.st = await call("PUT", `/reservas/${out.rid}/status`, { status: "InManagement" });
    out.h1conf = await call("PATCH", `/hotel-bookings/${out.h1Id}/status`, { status: "Confirmado" });
    // hotel2 NO se confirma: queda Solicitado.

    out.reservaAfter = await call("GET", `/reservas/${out.rid}`);
    out.statusAfter = out.reservaAfter.json?.status || null;

    return out;
  });

  const seedOk = [seed.sup, seed.r, seed.h1, seed.h2, seed.h1conf].every((r) => r && r.status < 300);
  check("Seed (operador P3 + reserva con hotel1 confirmado + hotel2 Solicitado)", seedOk,
    seedOk ? `sup=${seed.supId} reserva=${seed.numReserva || seed.rid}` :
      JSON.stringify({ sup: seed.sup?.text, r: seed.r?.text, h1: seed.h1?.text, h2: seed.h2?.text, h1conf: seed.h1conf?.text }).slice(0, 300));
  if (!seedOk) throw new Error("Seed fallo, no sigo");

  // La TRAMPA conocida: si esto da "Confirmed", el candado de reserva confirmada va a
  // tapar el guard nuevo y el resto del paseo no prueba lo que tiene que probar.
  check("Seed: la reserva NO quedó Confirmada (hotel2 la mantiene en gestión)",
    seed.statusAfter !== "Confirmed", `status=${seed.statusAfter}`);
  if (seed.statusAfter === "Confirmed") throw new Error("La reserva quedo Confirmada, el seed no armo la trampa correctamente");

  // ── Paso 1: pagar 200 al operador, imputados AL SERVICIO hotel1 (no a la reserva genérica) ──
  await page.goto(`${FRONT}/suppliers/${seed.supId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pago-paso-elegir"]');
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().waitFor();
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().click();
  await page.waitForSelector('[data-testid="pago-destino-fijado"]');

  // Selector "Servicio de la reserva (opcional)": SIN esto el pago queda imputado a la
  // reserva completa y GetCashPaidToOperatorForServiceAsync(hotel1) daría 0 — el guard de
  // esta tanda nunca dispararía. Localizamos el <select> por el bloque que contiene su label
  // (no tiene data-testid propio ni htmlFor/id, así que no sirve getByLabel).
  const bloqueServicio = page.locator("div.space-y-1").filter({ hasText: "Servicio de la reserva" });
  const selectorServicio = bloqueServicio.locator("select");
  await selectorServicio.waitFor({ timeout: 15000 });
  let opcionesServicio = [];
  for (let intento = 0; intento < 25; intento++) {
    opcionesServicio = await selectorServicio.locator("option").allTextContents();
    if (opcionesServicio.some((t) => t.includes(`Hotel P3 Uno ${seed.stamp}`))) break;
    await page.waitForTimeout(300);
  }
  const opcionHotel1 = opcionesServicio.find((t) => t.includes(`Hotel P3 Uno ${seed.stamp}`));
  check("Paso 1: el selector de servicio lista a hotel1 (y a hotel2, mismo operador)",
    Boolean(opcionHotel1), opcionesServicio.join(" | ").slice(0, 200));
  if (!opcionHotel1) throw new Error("No aparecio hotel1 en el selector de servicio del pago");
  await selectorServicio.selectOption({ label: opcionHotel1 });

  await page.locator('[data-testid="pago-monto"]').fill("200");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.locator('[data-testid="pago-exito"], [data-testid="pago-error"]').first().waitFor({ timeout: 15000 });
  if (await page.locator('[data-testid="pago-error"]').count()) {
    const err = await page.locator('[data-testid="pago-error"]').innerText();
    check("Paso 1: pago de 200 imputado a hotel1 registrado", false, err.slice(0, 200));
    throw new Error("Pago rechazado: " + err.slice(0, 200));
  }
  check("Paso 1: pago de 200 imputado a hotel1 registrado", true, "");

  // ── Paso 2: ir a la reserva, editar hotel1, bajar el costo a 25/noche (total $100 < $200) ──
  await page.goto(`${FRONT}/reservas/${seed.rid}`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Servicios" }).first().click().catch(async () => {
    await page.getByRole("tab", { name: "Servicios" }).first().click();
  });

  const botonEditarHotel1 = page.locator(`[data-testid="btn-edit-service-${seed.h1Id}"]`);
  await botonEditarHotel1.waitFor({ timeout: 15000 });
  await botonEditarHotel1.click();
  await page.waitForSelector('[data-testid="service-inline-card"]');
  await page.locator('[data-testid="hotel-costo-noche"]').fill("25");
  await page.locator('[data-testid="inline-card-guardar"]').click();

  // ── Paso 3: CHECK — cartel ámbar con el mensaje real del motor ──
  await page.locator('[data-testid="inline-card-confirmar-costo"]').waitFor({ timeout: 15000 });
  const aviso1 = await page.locator('[data-testid="inline-card-confirmar-costo"]').innerText();
  textosCapturados.push(aviso1);
  await page.screenshot({ path: SHOTS + "/1-cartel-ambar-confirmar-costo.png", fullPage: true });
  check("Paso 3: aparece el cartel ámbar de confirmación de costo", true, aviso1.slice(0, 220));
  check("Paso 3: el mensaje habla de saldo a favor", /saldo a favor/i.test(aviso1), aviso1.slice(0, 220));
  check("Paso 3: el mensaje trae el monto pagado en formato es-AR (200,00)", /200,00/.test(aviso1), aviso1.slice(0, 220));
  check("Paso 3: el mensaje trae el monto nuevo en formato es-AR (100,00)", /100,00/.test(aviso1), aviso1.slice(0, 220));
  check("Paso 3: sin jerga técnica ni GUIDs ni internals", !JERGA_INTERNA.test(aviso1), aviso1.slice(0, 220));
  check("Paso 3: sin formato gringo de plata (200.00)", !FORMATO_GRINGO.test(aviso1), "");

  // ── Paso 4: CHECK — "Volver a corregir" saca el cartel sin perder lo cargado ──
  await page.locator('[data-testid="confirmar-costo-corregir"]').click();
  await page.locator('[data-testid="inline-card-confirmar-costo"]').waitFor({ state: "detached", timeout: 10000 });
  const costoTrasCorregir = await page.locator('[data-testid="hotel-costo-noche"]').inputValue();
  const fichaSigueAbierta = (await page.locator('[data-testid="service-inline-card"]').count()) === 1;
  await page.screenshot({ path: SHOTS + "/2-volver-a-corregir.png", fullPage: true });
  check("Paso 4: 'Volver a corregir' saca el cartel y la ficha sigue abierta", fichaSigueAbierta, "");
  check("Paso 4: el valor cargado (25) sigue intacto en el campo de costo", costoTrasCorregir === "25", costoTrasCorregir);

  // ── Paso 5: guardar de nuevo → cartel de vuelta → "Sí, confirmar" → la ficha se cierra ──
  await page.locator('[data-testid="inline-card-guardar"]').click();
  await page.locator('[data-testid="inline-card-confirmar-costo"]').waitFor({ timeout: 15000 });
  const aviso2 = await page.locator('[data-testid="inline-card-confirmar-costo"]').innerText();
  textosCapturados.push(aviso2);
  check("Paso 5: el cartel vuelve a aparecer al reintentar guardar", true, aviso2.slice(0, 160));

  await page.locator('[data-testid="confirmar-costo-si"]').click();
  await page.locator('[data-testid="service-inline-card"]').waitFor({ state: "detached", timeout: 15000 });
  await page.screenshot({ path: SHOTS + "/3-confirmado-ficha-cerrada.png", fullPage: true });
  check("Paso 5: 'Sí, confirmar' guarda y CIERRA la ficha (sin toast extra)", true, "");

  // La fila de hotel1 tiene que mostrar el costo nuevo ($100,00 total, o 25/noche).
  const filaHotel1 = page.locator("tr").filter({ hasText: `Hotel P3 Uno ${seed.stamp}` }).first();
  await filaHotel1.waitFor({ timeout: 15000 });
  const textoFilaHotel1 = await filaHotel1.innerText();
  textosCapturados.push(textoFilaHotel1);
  check("Paso 5: la fila de hotel1 refleja el costo nuevo (100,00)", /100,00/.test(textoFilaHotel1), textoFilaHotel1.replace(/\n/g, " | ").slice(0, 200));

  // ── Paso 6: CHECK de sincronización — el saldo a favor del operador ya muestra $100,00 ──
  // (prueba de que el reconciliador corrió EN EL MOMENTO de la edición, sin esperar otro
  // pago/anulación posterior).
  await page.goto(`${FRONT}/suppliers/${seed.supId}/account`);
  await page.waitForLoadState("networkidle");
  let chipSaldoAFavor = page.locator('[data-testid="header-saldo-a-favor-ARS"]');
  await chipSaldoAFavor.waitFor({ timeout: 15000 });
  let textoChip = await chipSaldoAFavor.innerText();
  if (!/100,00/.test(textoChip)) {
    // Margen: si el header tarda un tick en refrescar tras la navegación, recargamos UNA vez.
    await page.reload();
    await page.waitForLoadState("networkidle");
    chipSaldoAFavor = page.locator('[data-testid="header-saldo-a-favor-ARS"]');
    await chipSaldoAFavor.waitFor({ timeout: 15000 });
    textoChip = await chipSaldoAFavor.innerText();
  }
  textosCapturados.push(textoChip);
  await page.screenshot({ path: SHOTS + "/4-saldo-a-favor-sincronizado.png", fullPage: true });
  check("Paso 6: el chip 'Saldo a favor' (ARS) del operador ya muestra $100,00 sin otro movimiento",
    /100,00/.test(textoChip), textoChip.replace(/\n/g, " | ").slice(0, 160));

  // ── Higiene global: nada de jerga interna, GUIDs ni formato gringo en TODO lo capturado ──
  const higieneOk = textosCapturados.every((t) => !JERGA_INTERNA.test(t) && !FORMATO_GRINGO.test(t));
  check("Higiene: ningún texto capturado tiene jerga interna, GUIDs ni formato gringo", higieneOk,
    higieneOk ? "" : textosCapturados.find((t) => JERGA_INTERNA.test(t) || FORMATO_GRINGO.test(t))?.slice(0, 200));

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
