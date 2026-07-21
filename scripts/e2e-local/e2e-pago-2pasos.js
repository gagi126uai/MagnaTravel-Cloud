// E2E real — rediseño "Registrar pago al proveedor" en 2 pasos (2026-07-21)
// Reemplaza el tramo de pago de e2e-t1.js (el selector pago-reserva ya no existe
// en el alta: ahora hay grilla Paso 1 + destino fijado en Paso 2 + cartel de exito).
const { chromium } = require("playwright-core");
const FRONT = "http://localhost:5173";
const SHOTS = __dirname + "/shots-pago2pasos";
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
  page.on("console", (m) => { if (m.type() === "error") console.log("CONSOLE_ERR:", m.text().slice(0, 300)); });
  page.on("response", (r) => { if (r.url().includes("/payments") && r.request().method() === "POST") console.log("POST_PAGO:", r.status(), r.url().slice(-60)); });

  // ── Login ──
  await page.goto(FRONT + "/login");
  await page.fill('input[type="email"]', "e2e@magnatravel.local");
  await page.fill('input[type="password"]', "E2eLocal2026!");
  await page.click('button[type="submit"]');
  await page.waitForURL((u) => !u.pathname.includes("login"), { timeout: 20000 });
  check("Login por UI", true, "");

  // ── Seed: operador + reserva con hotel USD confirmado (deuda de 500) ──
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
    const out = {};
    const stamp = Date.now().toString().slice(-6);
    out.stamp = stamp;
    out.sup = await call("POST", "/suppliers", { name: `Operador Pago2P ${stamp}`, defaultCurrency: "USD", invoicingMode: 0 });
    const supId = String(out.sup.json?.publicId || out.sup.json?.id);
    out.supId = supId;
    out.reserva = await call("POST", "/reservas", { name: `Reserva Pago2P ${stamp}` });
    const rid = out.reserva.json?.publicId || out.reserva.json?.id;
    out.rid = rid;
    out.numeroReserva = out.reserva.json?.numeroReserva || out.reserva.json?.fileNumber || null;
    out.hotel = await call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: `Hotel P2P ${stamp}`, starRating: 4, city: "Mendoza", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: 500, salePrice: 700, commission: 200,
      notes: null, currency: "USD",
      newCatalogProduct: { name: `Hotel P2P ${stamp}`, city: "Mendoza", supplierPublicId: supId },
    });
    out.hid = out.hotel.json?.publicId || out.hotel.json?.id;
    out.pax = await call("PATCH", `/reservas/${rid}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pas = await call("POST", `/reservas/${rid}/passengers`, {
      fullName: "Pasajero P2P", documentType: "DNI", documentNumber: "30777666",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.st = await call("PUT", `/reservas/${rid}/status`, { status: "InManagement" });
    out.hs = await call("PATCH", `/hotel-bookings/${out.hid}/status`, { status: "Confirmado" });
    return out;
  });
  check("Seed (operador + hotel USD 500 confirmado)",
    seed.sup.status < 300 && seed.hotel.status < 300 && seed.hs.status < 300, `sup=${seed.supId} r=${seed.rid}`);

  // ── Abrir la ficha del operador → Registrar pago → PASO 1 ──
  await page.goto(`${FRONT}/suppliers/${seed.supId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pago-paso-elegir"]');
  await page.screenshot({ path: SHOTS + "/1-paso1-grilla.png", fullPage: true });
  const grilla = await page.locator('[data-testid="pago-grilla-deuda"]').innerText();
  check("Paso 1: la grilla lista la reserva con su deuda USD",
    new RegExp(`Reserva Pago2P ${seed.stamp}|Hotel P2P ${seed.stamp}`).test(grilla) && /500/.test(grilla), grilla.slice(0, 120));
  check("Paso 1: existe el boton 'Pago a cuenta'",
    (await page.locator('[data-testid="pago-a-cuenta-boton"]').count()) === 1, "");

  // ── Elegir la fila → PASO 2 con destino fijado y monto precargado ──
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().click();
  await page.waitForSelector('[data-testid="pago-destino-fijado"]');
  const destino = await page.locator('[data-testid="pago-destino-fijado"]').innerText();
  const montoPre = await page.locator('[data-testid="pago-monto"]').inputValue();
  check("Paso 2: encabezado con el destino fijado", /Pag[aá]s/.test(destino) && destino.includes(seed.stamp), destino.slice(0, 120));
  check("Paso 2: monto precargado con la deuda (500)", montoPre === "500", montoPre);
  await page.screenshot({ path: SHOTS + "/2-paso2-destino.png", fullPage: true });

  // ── Pagar 200 (parcial) → cartel de exito con el impacto real ──
  await page.locator('[data-testid="pago-monto"]').fill("200");
  await page.locator('[data-testid="pago-confirmar"]').click();
  // Si el POST rechaza, mostrar el cartel de error real en vez de un timeout mudo.
  try {
    await page.locator('[data-testid="pago-exito"], [data-testid="pago-error"]').first()
      .waitFor({ timeout: 15000 });
  } catch (e) {
    const btnDeshab = await page.locator('[data-testid="pago-confirmar"]').isDisabled().catch(() => "?");
    const btnCount = await page.locator('[data-testid="pago-confirmar"]').count();
    await page.screenshot({ path: SHOTS + "/x-timeout.png", fullPage: true });
    console.log(`DEBUG_TIMEOUT: confirmar count=${btnCount} disabled=${btnDeshab}`);
    throw e;
  }
  if (await page.locator('[data-testid="pago-error"]').count()) {
    const err = await page.locator('[data-testid="pago-error"]').innerText();
    await page.screenshot({ path: SHOTS + "/x-error-post.png", fullPage: true });
    check("Exito: el POST fue aceptado", false, "CARTEL DE ERROR: " + err.slice(0, 200));
    throw new Error("POST rechazado: " + err.slice(0, 200));
  }
  const exito = await page.locator('[data-testid="pago-exito"]').innerText();
  await page.screenshot({ path: SHOTS + "/3-cartel-exito.png", fullPage: true });
  check("Exito: el cartel dice que BAJO LA DEUDA de la reserva",
    /Baj[oó] la deuda/i.test(exito) && exito.includes(seed.stamp || ""), exito.slice(0, 160));
  check("Exito: dice cuanto queda pendiente (300)", /300/.test(exito), exito.slice(0, 160));
  check("Exito: sin internals (GUID/undefined/null)",
    !/[0-9a-f]{8}-[0-9a-f]{4}-|undefined|NaN|\bnull\b/i.test(exito), "");

  // ── "Ver cuenta" → el extracto nombra la reserva imputada ──
  await page.locator('[data-testid="pago-exito-ver-cuenta"]').click();
  await page.waitForTimeout(2000);
  const body = await page.locator("body").innerText();
  check("Extracto: la linea del pago nombra la reserva imputada",
    new RegExp(`Reserva .*${seed.stamp}|${seed.numeroReserva || "###"}`).test(body), "");
  await page.screenshot({ path: SHOTS + "/4-extracto-con-destino.png", fullPage: true });

  // ── Camino "Pago a cuenta": Paso 1 → boton → Paso 2 con aviso → confirmar ──
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pago-paso-elegir"]');
  await page.locator('[data-testid="pago-a-cuenta-boton"]').click();
  await page.waitForSelector('[data-testid="pago-destino-fijado"]');
  const destinoCta = await page.locator('[data-testid="pago-destino-fijado"]').innerText();
  check("A cuenta: el destino fijado lo dice explicito", /a cuenta|sin imputar/i.test(destinoCta), destinoCta.slice(0, 100));
  await page.locator('[data-testid="pago-monto"]').fill("50");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.waitForSelector('[data-testid="pago-exito"]', { timeout: 15000 });
  const exitoCta = await page.locator('[data-testid="pago-exito"]').innerText();
  check("A cuenta: el cartel dice 'saldo a favor', sin inventar reserva",
    /saldo a favor/i.test(exitoCta) && !/Baj[oó] la deuda/i.test(exitoCta), exitoCta.slice(0, 140));
  await page.screenshot({ path: SHOTS + "/5-a-cuenta-exito.png", fullPage: true });

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
