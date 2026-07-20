// E2E real Tanda 1 — contrato pantalla-motor (pagos a proveedor)
// Topologia same-origin: Vite 5173 con proxy /api -> API .NET 60663 (como PROD tras nginx).
const { chromium } = require("playwright-core");
const FRONT = "http://localhost:5173";
const SHOTS = __dirname + "/shots";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail : ""}`);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
  page.setDefaultTimeout(15000);
  page.on("console", (m) => { if (m.type() === "error") console.log("CONSOLE_ERR:", m.text().slice(0, 200)); });

  // ── Login por la UI real ──
  await page.goto(FRONT + "/login");
  await page.fill('input[type="email"]', "e2e@magnatravel.local");
  await page.fill('input[type="password"]', "E2eLocal2026!");
  await page.click('button[type="submit"]');
  await page.waitForURL((u) => !u.pathname.includes("login"), { timeout: 20000 });
  check("Login por UI", true, page.url());

  // ── Sembrado por fetch same-origin (mismas cookies/CSRF que la app) ──
  const seed = await page.evaluate(async () => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const call = async (method, path, body) => {
      const r = await fetch("/api" + path, {
        method,
        credentials: "include",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
        body: body ? JSON.stringify(body) : undefined,
      });
      const text = await r.text();
      let json = null;
      try { json = JSON.parse(text); } catch {}
      return { status: r.status, json, text: text.slice(0, 200) };
    };

    const out = { csrfPresente: Boolean(csrf) };
    out.supA = await call("POST", "/suppliers", { name: "Operador Mayorista E2E", defaultCurrency: "USD", invoicingMode: 0 });
    out.supB = await call("POST", "/suppliers", { name: "Operador Directo E2E", invoicingMode: 1 });
    out.reserva = await call("POST", "/reservas", { name: "Reserva E2E T1" });
    const rid = out.reserva.json?.publicId || out.reserva.json?.id;
    out.rid = rid;
    out.hotel = await call("POST", `/reservas/${rid}/hotels`, {
      supplierId: String(out.supA.json?.publicId || out.supA.json?.id),
      hotelName: "Hotel Maitei E2E", starRating: 4, city: "Asuncion", country: "PY",
      checkIn: "2026-09-10T00:00:00Z", checkOut: "2026-09-15T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: 500, salePrice: 650, commission: 150,
      notes: null, currency: "USD",
      newCatalogProduct: { name: "Hotel Maitei E2E", city: "Asuncion", supplierPublicId: String(out.supA.json?.publicId || out.supA.json?.id) },
    });
    const hid = out.hotel.json?.publicId || out.hotel.json?.id;
    out.hid = hid;
    // El motor exige declarar cantidad de pasajeros y cargar al menos 1 antes de "En gestión"
    out.paxCounts = await call("PATCH", `/reservas/${rid}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pasajero = await call("POST", `/reservas/${rid}/passengers`, {
      fullName: "Pasajero E2E", documentType: "DNI", documentNumber: "30111222",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    // ADR-020: la reserva pasa a "En gestión" a mano; Confirmed lo pone solo el motor
    out.confirmReserva = await call("PUT", `/reservas/${rid}/status`, { status: "InManagement" });
    out.confirmHotel = await call("PATCH", `/hotel-bookings/${hid}/status`, { status: "Confirmado" });

    // Segunda reserva con hotel en ARS: el operador queda multimoneda (deuda USD + ARS)
    // y el link "pagar en otra moneda" aparece — el caso trampa se puede armar en pantalla.
    out.reserva2 = await call("POST", "/reservas", { name: "Reserva E2E ARS" });
    const rid2 = out.reserva2.json?.publicId || out.reserva2.json?.id;
    out.hotel2 = await call("POST", `/reservas/${rid2}/hotels`, {
      supplierId: String(out.supA.json?.publicId || out.supA.json?.id),
      hotelName: "Hotel Pesos E2E", starRating: 3, city: "Cordoba", country: "AR",
      checkIn: "2026-10-01T00:00:00Z", checkOut: "2026-10-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: 80000, salePrice: 100000, commission: 20000,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: "Hotel Pesos E2E", city: "Cordoba", supplierPublicId: String(out.supA.json?.publicId || out.supA.json?.id) },
    });
    const hid2 = out.hotel2.json?.publicId || out.hotel2.json?.id;
    out.paxCounts2 = await call("PATCH", `/reservas/${rid2}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pasajero2 = await call("POST", `/reservas/${rid2}/passengers`, {
      fullName: "Pasajero E2E Dos", documentType: "DNI", documentNumber: "30111333",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.confirmReserva2 = await call("PUT", `/reservas/${rid2}/status`, { status: "InManagement" });
    out.confirmHotel2 = await call("PATCH", `/hotel-bookings/${hid2}/status`, { status: "Confirmado" });
    return out;
  });
  console.log("SEED:", JSON.stringify({
    csrf: seed.csrfPresente, supA: seed.supA.status, supB: seed.supB.status,
    reserva: seed.reserva.status, hotel: [seed.hotel.status, seed.hotel.text.slice(0, 60)],
    pasajero: [seed.pasajero?.status, seed.pasajero?.text],
    confirmReserva: [seed.confirmReserva.status, seed.confirmReserva.text],
    confirmHotel: [seed.confirmHotel.status, seed.confirmHotel.text],
  }));
  const supAId = seed.supA.json?.publicId || seed.supA.json?.id;
  const supBId = seed.supB.json?.publicId || seed.supB.json?.id;
  check("Seed base (2 operadores + reserva + hotel USD)",
    seed.supA.status < 300 && seed.supB.status < 300 && seed.reserva.status < 300 && seed.hotel.status < 300,
    `A=${supAId} B=${supBId} R=${seed.rid} H=${seed.hid}`);

  // ── T1-lateral: operador Intermediación → SIN botón "Nueva factura" ──
  await page.goto(`${FRONT}/suppliers/${supBId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByText("Facturas operador").first().click();
  await page.waitForTimeout(1200);
  await page.screenshot({ path: SHOTS + "/1-supB-intermediacion.png", fullPage: true });
  const nuevaFacturaB = await page.getByText("Nueva factura", { exact: true }).count();
  check("Intermediación: 'Nueva factura' ESCONDIDO", nuevaFacturaB === 0, `count=${nuevaFacturaB}`);

  // ── Control: operador compra/reventa SÍ muestra "Nueva factura" ──
  await page.goto(`${FRONT}/suppliers/${supAId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByText("Facturas operador").first().click();
  await page.waitForTimeout(1200);
  await page.screenshot({ path: SHOTS + "/2-supA-normal.png", fullPage: true });
  const nuevaFacturaA = await page.getByText("Nueva factura", { exact: true }).count();
  check("Compra/reventa: 'Nueva factura' visible", nuevaFacturaA >= 1, `count=${nuevaFacturaA}`);
  await page.getByText("Cuenta corriente").first().click();
  await page.waitForTimeout(800);

  // ── Ficha "Registrar pago" ──
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pagar-proveedor-inline"]');
  await page.screenshot({ path: SHOTS + "/3-ficha-pago.png", fullPage: true });

  // Imputar a una reserva
  const imputar = page.locator('[data-testid="pago-imputar-a"]');
  if (await imputar.count()) {
    const tagName = await imputar.evaluate((el) => el.tagName);
    if (tagName === "SELECT") {
      const opts = await imputar.locator("option").allTextContents();
      console.log("IMPUTAR_OPCIONES:", JSON.stringify(opts));
      await imputar.selectOption({ index: opts.findIndex((o) => /reserva/i.test(o)) });
    } else {
      await imputar.click();
    }
  }
  await page.waitForTimeout(400);

  // Elegir la reserva sembrada
  const selReserva = page.locator('[data-testid="pago-reserva"]');
  await selReserva.waitFor();
  const opcionesReserva = await selReserva.locator("option").allTextContents();
  console.log("RESERVAS_OPCIONES:", JSON.stringify(opcionesReserva));
  const idx = opcionesReserva.findIndex((o) => o.includes("Reserva E2E T1"));
  await selReserva.selectOption({ index: idx >= 0 ? idx : 1 });
  await page.waitForTimeout(1200); // fetch de servicios de la reserva
  await page.screenshot({ path: SHOTS + "/4-reserva-elegida.png", fullPage: true });

  // Moneda por defecto = $ pesos (moneda principal del operador: mayor deuda).
  // La reserva elegida (T1) debe en USD → el hotel USD NO se lista + aviso por moneda.
  await page.screenshot({ path: SHOTS + "/5-moneda-ars.png", fullPage: true });
  const bodyConArs = await page.locator('[data-testid="pagar-proveedor-inline"]').innerText();
  check("Pre-chequeo (a): con pago $ el hotel USD NO se lista",
    !/Hotel Maitei/i.test(bodyConArs), "");
  check("Aviso 'no hay servicios en la moneda del pago' visible",
    /en la moneda del pago/i.test(bodyConArs), "");
  // Dejar visible el selector de moneda para el paso final
  const linkOtraMoneda = page.locator('[data-testid="pago-link-otra-moneda"]');
  if (await linkOtraMoneda.count()) await linkOtraMoneda.click();
  const selMoneda = page.locator('[data-testid="pago-moneda"]');
  await selMoneda.waitFor();
  const monedas = await selMoneda.locator("option").allTextContents();
  console.log("MONEDAS:", JSON.stringify(monedas));

  // ── EL CENTRO DE LA TANDA: el cartel muestra el mensaje REAL del motor ──
  // Pago ARS imputado a la reserva (sin servicio): la deuda es solo USD → mensaje 4.
  await page.locator('[data-testid="pago-monto"]').fill("1000");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.waitForSelector('[data-testid="pago-error"]', { timeout: 10000 });
  const cartel = await page.locator('[data-testid="pago-error"]').innerText();
  await page.screenshot({ path: SHOTS + "/6-cartel-mensaje-real.png", fullPage: true });
  console.log("CARTEL:", JSON.stringify(cartel));
  check("Cartel muestra el mensaje REAL del motor (mensaje 4)",
    cartel.includes("El pago no coincide con ninguna moneda de la deuda de este proveedor en la reserva."), cartel.slice(0, 140));
  check("Cartel NO es el genérico viejo",
    !cartel.includes("No se pudo registrar el pago al proveedor"), "");
  check("Cartel sin internals (Parameter/exception/inglés)",
    !/Parameter|Exception|stack|SqlState/i.test(cartel), "");

  // La ficha quedó intacta (monto sigue cargado) — regla "nunca se pierde lo cargado"
  const montoDespues = await page.locator('[data-testid="pago-monto"]').inputValue();
  check("Ficha intacta tras el error (monto conservado)", montoDespues === "1000", montoDespues);

  // ── Camino feliz + H1 (sin bloqueo falso): pasar a USD, elegir hotel, pagar ──
  await selMoneda.selectOption({ index: monedas.findIndex((m) => /USD|d[oó]lar/i.test(m)) });
  // Imputar a la deuda en US$ (si no, es pago cruzado con tipo de cambio — otro flujo)
  const selImputar = page.locator('[data-testid="pago-imputar-a"]');
  if (await selImputar.count()) {
    const impOpts = await selImputar.locator("option").allTextContents();
    const usdIdx = impOpts.findIndex((o) => /US\$/.test(o));
    if (usdIdx >= 0) await selImputar.selectOption({ index: usdIdx });
  }
  await page.waitForTimeout(400);
  // La reserva se resetea al cambiar de moneda: volver a elegir la T1 (USD)
  const opciones2 = await selReserva.locator("option").allTextContents();
  await selReserva.selectOption({ index: opciones2.findIndex((o) => o.includes("Reserva E2E T1")) });
  await page.waitForTimeout(1500);
  await page.locator("option", { hasText: "Hotel Maitei E2E" }).first().waitFor({ state: "attached", timeout: 10000 });
  check("Pre-chequeo (a): con pago USD el hotel USD SÍ se lista", true, "");
  const confirmarHabilitado = await page.locator('[data-testid="pago-confirmar"]').isEnabled();
  check("H1: con servicios reales el Confirmar NO queda bloqueado", confirmarHabilitado, "");
  // elegir el hotel en el selector de servicio (select "Servicio de la reserva")
  const servSel = page.locator("select", { has: page.locator("option", { hasText: "Hotel Maitei" }) }).first();
  const servOpts = await servSel.locator("option").allTextContents();
  await servSel.selectOption({ index: servOpts.findIndex((o) => /Hotel Maitei/.test(o)) });
  await page.locator('[data-testid="pago-monto"]').fill("500");
  await page.screenshot({ path: SHOTS + "/7-antes-pago-usd.png", fullPage: true });
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.waitForTimeout(2500);
  await page.screenshot({ path: SHOTS + "/8-despues-pago-usd.png", fullPage: true });
  const bodyFinal = await page.locator("body").innerText();
  const pagoRegistrado = /Pago registrado/i.test(bodyFinal);
  const cartelFinal = (await page.locator('[data-testid="pago-error"]').count())
    ? await page.locator('[data-testid="pago-error"]').innerText() : "";
  check("Camino feliz: pago USD imputado al hotel se registra", pagoRegistrado, cartelFinal.slice(0, 140));

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
