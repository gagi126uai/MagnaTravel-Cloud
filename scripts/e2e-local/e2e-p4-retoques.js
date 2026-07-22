// E2E real — Tanda P4 "circuito proveedor": 3 retoques de pantalla + 1 guard backend nuevo
// (2026-07-22).
//
// Camina con la app real corriendo (API relanzada con el binario NUEVO, ver README):
//   (1) Banner del candado para Admin: el texto de la franja ámbar distingue entre "pedí
//       autorización" (vendedor) y "podés destrabarla" (admin, mismo permiso que ya usa el
//       modal). El botón abre el MISMO modal de siempre.
//   (2) Carteles no apilados al anular: cuando el motor rechaza la anulación (409/400), el
//       cartel del CASO (verde/celeste/ámbar) se oculta — solo queda el cartel de error a la
//       vista. Antes de esta tanda los dos convivían, mensaje confuso.
//   (3) Guard del normalizador (backend NUEVO): al pasar de Presupuesto a "cliente aceptó"
//       (PUT status InManagement), si algún servicio venía Confirmado con plata pagada al
//       operador y SIN factura que ancle el reembolso, el motor ahora rechaza TODA la
//       transición (antes bajaba el estado en silencio, dejando esa plata sin rastro).
//   (4) Eliminar con recibo anulado: antes, un recibo anulado ocultaba Editar Y Eliminar por
//       completo. Ahora cada botón se gobierna por su propio candado: Eliminar sigue
//       habilitado (la regla de negocio nunca lo bloqueó), Editar se ve gris con el motivo.
//
// Requiere el entorno de scripts/e2e-local/README.md (DB + API + vite e2e) MÁS Postgres local
// para el bypass SQL del check (3) — el hotel "Confirmado" con la reserva todavía en
// Presupuesto NO se puede lograr por la UI/API normal (ShouldForceSolicitadoStatusAsync lo
// fuerza a "Solicitado" mientras la reserva esté en Presupuesto); es justo el escenario de
// "bypass de API o data preexistente" que el guard nuevo defiende.

const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const http = require("http");

const FRONT = "http://localhost:5173";
const API = "http://localhost:59663";
const SHOTS = __dirname + "/shots-p4";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail.toString().slice(0, 220) : ""}`);
}

// ── Helper: espera en loop a que la API responda algo (arriba compilando ahora mismo) ──
function esperarApi(timeoutMs = 150000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const intento = () => {
      const req = http.get(API + "/api/health", (res) => {
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

// ── Helper: psql directo contra el Postgres local (mismo patron que e2e-p2) ──
function psql(sqlText) {
  return execFileSync(
    "docker",
    ["exec", "travel_db_local", "psql", "-U", "traveluser", "-d", "travel", "-t", "-A", "-q", "-v", "ON_ERROR_STOP=1", "-c", sqlText],
    { encoding: "utf8" }
  ).trim();
}
function psqlInt(sqlText) {
  const raw = psql(sqlText);
  const n = parseInt(raw, 10);
  if (Number.isNaN(n)) throw new Error(`psqlInt: no pude parsear un entero de "${raw}" (SQL: ${sqlText.slice(0, 120)})`);
  return n;
}

// Textos que NUNCA deberían aparecer en algo que ve el vendedor: jerga interna, GUIDs,
// nombres de excepciones .NET, codigos crudos, formato de plata "gringo" (punto decimal).
const JERGA_INTERNA = /allocation|Reassociate|Exception|ServiceRecordKind|CANCEL_SERVICE|ANNUL_CREDIT|undefined|null\b|NormalizeAllServices|InvalidOperationException/i;
const GUID_RE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const FORMATO_GRINGO = /\d\.\d{2}\b/;
const textosCapturados = [];

(async () => {
  console.log("Esperando a que la API responda (relanzada con el binario nuevo)...");
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

  // Helper de fetch same-origin (mismas cookies/CSRF que la app), reusado en todos los seeds.
  const apiCall = (method, path, body) => page.evaluate(async ({ method, path, body }) => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const r = await fetch("/api" + path, {
      method, credentials: "include",
      headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await r.text();
    let json = null; try { json = JSON.parse(text); } catch {}
    return { status: r.status, json, text: text.slice(0, 300) };
  }, { method, path, body });

  // ═══════════════════════════════════════════════════════════════════════════
  // SEED A (checks 1 y 2): operador + reserva con UN hotel confirmado ($500) que
  // queda como único servicio → la reserva pasa a "Confirmada" por estados
  // derivados apenas se confirma el hotel (mismo patron que e2e-p1).
  // ═══════════════════════════════════════════════════════════════════════════
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
      supplierId: supId, hotelName: nombre, starRating: 4, city: "Ushuaia", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: costo, salePrice: costo + 200, commission: 100,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: nombre, city: "Ushuaia", supplierPublicId: supId },
    });

    out.sup = await call("POST", "/suppliers", { name: `Operador P4 ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supId = String(out.sup.json?.publicId || out.sup.json?.id);
    // Reserva D usa un operador APARTE (no "Operador P4"): si compartiera el mismo operador
    // que la reserva A, la grilla de deuda del check (2) mostraría DOS filas y el pago
    // terminaría imputado a la reserva equivocada según el orden en que las devuelva el
    // backend (no determinístico) — bug real que este seed evita de raíz.
    out.supD = await call("POST", "/suppliers", { name: `Operador P4D ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supDId = String(out.supD.json?.publicId || out.supD.json?.id);

    // Reserva A: la que se usa para los checks (1) banner y (2) carteles de anular.
    out.rA = await call("POST", "/reservas", { name: `Reserva P4A ${stamp}` });
    out.ridA = out.rA.json?.publicId || out.rA.json?.id;
    out.numA = out.rA.json?.numeroReserva || out.rA.json?.fileNumber || null;
    out.hA = await mkHotel(out.ridA, out.supId, `Hotel P4A ${stamp}`, 500);
    out.hAId = out.hA.json?.publicId || out.hA.json?.id;
    out.paxA = await call("PATCH", `/reservas/${out.ridA}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
    out.pasA = await call("POST", `/reservas/${out.ridA}/passengers`, {
      fullName: "Pasajero P4A", documentType: "DNI", documentNumber: "30444555",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.stA = await call("PUT", `/reservas/${out.ridA}/status`, { status: "InManagement" });
    out.hsA = await call("PATCH", `/hotel-bookings/${out.hAId}/status`, { status: "Confirmado" });
    out.reservaAAfter = await call("GET", `/reservas/${out.ridA}`);
    out.statusAAfter = out.reservaAAfter.json?.status || null;

    // Reserva D: independiente, para el check (4) — cobro con recibo anulado.
    out.rD = await call("POST", "/reservas", { name: `Reserva P4D ${stamp}` });
    out.ridD = out.rD.json?.publicId || out.rD.json?.id;
    out.hD = await mkHotel(out.ridD, out.supDId, `Hotel P4D ${stamp}`, 300);
    out.hDId = out.hD.json?.publicId || out.hD.json?.id;
    out.paxD = await call("PATCH", `/reservas/${out.ridD}/passenger-counts`, { adultCount: 1, childCount: 0, infantCount: 0 });
    out.pasD = await call("POST", `/reservas/${out.ridD}/passengers`, {
      fullName: "Pasajero P4D", documentType: "DNI", documentNumber: "30666777",
      birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
    });
    out.stD = await call("PUT", `/reservas/${out.ridD}/status`, { status: "InManagement" });
    out.hsD = await call("PATCH", `/hotel-bookings/${out.hDId}/status`, { status: "Confirmado" });
    out.payD = await call("POST", "/payments", {
      reservaId: out.ridD, amount: 150, currency: "ARS", method: "Transferencia",
      paidAt: new Date().toISOString(), notes: "Cobro E2E P4-4",
    });
    out.pidD = out.payD.json?.publicId || out.payD.json?.id;
    out.receiptD = await call("POST", `/payments/${out.pidD}/receipt`);
    out.voidD = await call("POST", `/payments/${out.pidD}/receipt/void`, { reason: null });

    return out;
  });
  const seedOk = [seed.sup, seed.supD, seed.hA, seed.hsA, seed.hD, seed.hsD, seed.payD, seed.receiptD, seed.voidD]
    .every((r) => r && r.status < 300);
  check("Seed A+D (operador + reserva confirmada + reserva con recibo anulado)", seedOk,
    seedOk ? `A=${seed.ridA} D=${seed.ridD}` : JSON.stringify({
      hA: seed.hA?.text, hsA: seed.hsA?.text, hD: seed.hD?.text, hsD: seed.hsD?.text,
      payD: seed.payD?.text, receiptD: seed.receiptD?.text, voidD: seed.voidD?.text,
    }).slice(0, 400));
  if (!seedOk) throw new Error("Seed fallo, no sigo");
  check("Seed: la reserva A quedó Confirmada (único servicio confirmado → estados derivados)",
    seed.statusAAfter === "Confirmed", `status=${seed.statusAAfter}`);
  if (seed.statusAAfter !== "Confirmed") throw new Error("La reserva A no quedó Confirmada, el check (1) no probaría el candado real");

  // ═══════════════════════════════════════════════════════════════════════════
  // CHECK (1): banner del candado para Admin — "Podés destrabarla" + "Destrabar reserva"
  // (NO "Pedí autorización"), y el botón abre el mismo modal de siempre.
  // ═══════════════════════════════════════════════════════════════════════════
  await page.goto(`${FRONT}/reservas/${seed.ridA}`);
  await page.waitForLoadState("networkidle");
  const banner = page.locator('[data-testid="reserva-lock-banner"]');
  await banner.waitFor({ timeout: 15000 });
  const textoBanner = (await banner.innerText()).trim();
  textosCapturados.push(textoBanner);
  await page.screenshot({ path: SHOTS + "/1-banner-admin.png", fullPage: true });
  check("(1) El banner (Admin) dice 'Podés destrabarla para editar'",
    /Podés destrabarla para editar/.test(textoBanner), textoBanner);
  check("(1) El banner (Admin) NO dice 'Pedí autorización'",
    !/Pedí autorización/i.test(textoBanner), textoBanner);
  const botonBanner = page.locator('[data-testid="reserva-request-edit-btn"]');
  const textoBoton = (await botonBanner.innerText()).trim();
  check("(1) El botón del banner dice 'Destrabar reserva' (no 'Pedí autorización')",
    textoBoton === "Destrabar reserva", textoBoton);

  await botonBanner.click();
  const modal = page.getByRole("dialog", { name: "Reserva bloqueada" });
  await modal.waitFor({ timeout: 10000 });
  await page.screenshot({ path: SHOTS + "/2-modal-autorizacion.png", fullPage: true });
  check("(1) El click abre el modal de autorización de siempre", await modal.isVisible(), "");
  // Cerramos con el botón "Cerrar" (la X) para dejar la reserva intacta antes del check (2).
  // (Escape no sirve acá: el listener vive en el <div role="dialog">, que no tiene foco propio
  // salvo que el usuario haya clickeado adentro primero.)
  await modal.getByRole("button", { name: "Cerrar" }).click();
  await modal.waitFor({ state: "detached", timeout: 10000 });

  // ═══════════════════════════════════════════════════════════════════════════
  // CHECK (2): carteles no apilados al anular. Le pagamos 200 al operador imputados
  // AL SERVICIO hotel (no a la reserva en general, mismo patrón que e2e-p3) para que
  // el motor rechace "Anular reserva" con el freno de plata R1
  // (ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND) — es un rechazo REAL del motor, no un
  // estado simulado en el front.
  // ═══════════════════════════════════════════════════════════════════════════
  await page.goto(`${FRONT}/suppliers/${seed.supId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: "Registrar pago" }).first().click();
  await page.waitForSelector('[data-testid="pago-paso-elegir"]');
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().waitFor();
  await page.locator('[data-testid^="pago-elegir-fila-"]').first().click();
  await page.waitForSelector('[data-testid="pago-destino-fijado"]');

  // Selector "Servicio de la reserva (opcional)": imputamos el pago AL hotel, no a la
  // reserva en general — así el guard R1 ve plata pagada a ESE servicio puntual.
  const bloqueServicio = page.locator("div.space-y-1").filter({ hasText: "Servicio de la reserva" });
  const selectorServicio = bloqueServicio.locator("select");
  await selectorServicio.waitFor({ timeout: 15000 });
  let opcionesServicio = [];
  for (let intento = 0; intento < 25; intento++) {
    opcionesServicio = await selectorServicio.locator("option").allTextContents();
    if (opcionesServicio.some((t) => t.includes(`Hotel P4A ${seed.stamp}`))) break;
    await page.waitForTimeout(300);
  }
  const opcionHotel = opcionesServicio.find((t) => t.includes(`Hotel P4A ${seed.stamp}`));
  check("(2) El selector de servicio del pago lista al hotel de la reserva A",
    Boolean(opcionHotel), opcionesServicio.join(" | ").slice(0, 200));
  if (!opcionHotel) throw new Error("No aparecio el hotel en el selector de servicio del pago");
  await selectorServicio.selectOption({ label: opcionHotel });

  await page.locator('[data-testid="pago-monto"]').fill("200");
  await page.locator('[data-testid="pago-confirmar"]').click();
  await page.locator('[data-testid="pago-exito"], [data-testid="pago-error"]').first().waitFor({ timeout: 15000 });
  if (await page.locator('[data-testid="pago-error"]').count()) {
    const err = await page.locator('[data-testid="pago-error"]').innerText();
    check("(2) Pago de 200 imputado al hotel de la reserva A registrado", false, err.slice(0, 200));
    throw new Error("Pago rechazado: " + err.slice(0, 200));
  }
  check("(2) Pago de 200 imputado al hotel de la reserva A registrado", true, "");

  // Abrimos "Anular reserva" desde la ficha (solapa Estado de Cuenta).
  await page.goto(`${FRONT}/reservas/${seed.ridA}`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: /Estado de Cuenta/i }).first().click();
  await page.waitForTimeout(800);
  // Hay DOS botones con este testid (el del header sticky y el de la barra de Estado de
  // Cuenta): los dos llaman al mismo handler, usamos el primero que aparezca.
  await page.locator('[data-testid="btn-anular-reserva"]').first().click();

  // Antes de enviar: el cartel del CASO (verde, DirectCancel — no hay cobros del cliente
  // todavía) tiene que estar solo, sin el cartel de error (todavía no se mandó nada).
  const cartelCasoVerde = page.locator('[data-testid="cancelar-banner-sin-factura"]');
  await cartelCasoVerde.waitFor({ timeout: 10000 });
  check("(2) Antes de enviar: se ve el cartel del caso (DirectCancel, verde)",
    await cartelCasoVerde.isVisible(), "");
  check("(2) Antes de enviar: NO hay cartel de error todavía",
    (await page.locator('[data-testid="cancelar-inline-conflict-msg"]').count()) === 0, "");

  // Motivo válido (≥10 caracteres) y enviar — el motor va a rechazar por el freno de plata R1.
  await page.locator('[data-testid="cancelar-inline-reason-textarea"]').fill("El cliente decidió no viajar más, se anula todo");
  // El botón "Anular reserva" del header sticky sigue visible con el panel abierto, así que
  // usamos el testid del botón de ENVÍO del panel (no el que lo abrió).
  await page.locator('[data-testid="cancelar-inline-confirm-btn"]').click();

  // El motor tarda un instante en resolver el rechazo (calcula el RefundCap del operador
  // antes de contestar); 20s da margen sin caer en un timeout ajustado de más.
  const cartelError = page.locator('[data-testid="cancelar-inline-conflict-msg"]');
  await cartelError.waitFor({ timeout: 20000 });
  const textoCartelError = (await cartelError.innerText()).trim();
  textosCapturados.push(textoCartelError);
  await page.screenshot({ path: SHOTS + "/3-anular-solo-cartel-error.png", fullPage: true });

  check("(2) Al fallar: aparece el cartel de error con el motivo real del motor",
    /operador/i.test(textoCartelError) && /factura/i.test(textoCartelError), textoCartelError);
  check("(2) Al fallar: el cartel del caso (verde) YA NO se ve — no quedan apilados",
    (await page.locator('[data-testid="cancelar-banner-sin-factura"]').count()) === 0, "");
  check("(2) Al fallar: tampoco se ven los otros carteles de caso (celeste/ámbar)",
    (await page.locator('[data-testid="cancelar-banner-saldo-favor"]').count()) === 0 &&
    (await page.locator('[data-testid="cancelar-banner-con-factura"]').count()) === 0, "");
  check("(2) El cartel de error: sin jerga técnica ni GUIDs ni códigos crudos",
    !JERGA_INTERNA.test(textoCartelError) && !GUID_RE.test(textoCartelError), textoCartelError);

  // ═══════════════════════════════════════════════════════════════════════════
  // CHECK (3): guard del normalizador (backend NUEVO). Reserva B se queda en
  // Presupuesto (nunca se manda a InManagement por el camino normal): el hotel se
  // fuerza a "Confirmado" por SQL directo porque, mientras la reserva esté en
  // Presupuesto, la API SIEMPRE degrada cualquier intento de confirmar un servicio a
  // "Solicitado" (ShouldForceSolicitadoStatusAsync) — el escenario que este guard
  // defiende es EXACTAMENTE ese bypass (API directa / data preexistente).
  // ═══════════════════════════════════════════════════════════════════════════
  const seedB = await page.evaluate(async () => {
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
    const stamp = Date.now().toString().slice(-6) + "b";
    const out = { stamp };

    const mkHotel = (rid, supId, nombre, costo) => call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: nombre, starRating: 4, city: "Salta", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: costo, salePrice: costo + 200, commission: 100,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: nombre, city: "Salta", supplierPublicId: supId },
    });

    out.sup = await call("POST", "/suppliers", { name: `Operador P4B ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supId = String(out.sup.json?.publicId || out.sup.json?.id);

    // Reserva B: BLOQUEA (hotel confirmado + pagado + sin factura).
    out.rB = await call("POST", "/reservas", { name: `Reserva P4B ${stamp}` });
    out.ridB = out.rB.json?.publicId || out.rB.json?.id;
    out.hB = await mkHotel(out.ridB, out.supId, `Hotel P4B ${stamp}`, 500);
    out.hBId = out.hB.json?.publicId || out.hB.json?.id;
    out.paxB = await call("PATCH", `/reservas/${out.ridB}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });

    // Reserva C: ESCAPE — hotel confirmado (mismo bypass SQL) pero SIN pago al operador.
    // Prueba que el guard bloquea por la PLATA, no por el mero hecho de estar Confirmado.
    out.rC = await call("POST", "/reservas", { name: `Reserva P4C ${stamp}` });
    out.ridC = out.rC.json?.publicId || out.rC.json?.id;
    out.hC = await mkHotel(out.ridC, out.supId, `Hotel P4C ${stamp}`, 400);
    out.hCId = out.hC.json?.publicId || out.hC.json?.id;
    out.paxC = await call("PATCH", `/reservas/${out.ridC}/passenger-counts`, { adultCount: 1, childCount: 0, infantCount: 0 });

    return out;
  });
  const seedBOk = [seedB.sup, seedB.hB, seedB.hC].every((r) => r && r.status < 300);
  check("Seed B/C (operador + 2 reservas en Presupuesto con hotel)", seedBOk,
    seedBOk ? `B=${seedB.ridB} C=${seedB.ridC}` : JSON.stringify({ hB: seedB.hB?.text, hC: seedB.hC?.text }).slice(0, 300));
  if (!seedBOk) throw new Error("Seed B/C fallo, no sigo");

  // Bypass SQL: forzamos el hotel de B y C a "Confirmado" mientras la reserva sigue en
  // Presupuesto — el estado que la API normal jamás deja lograr (ver nota arriba).
  let dbIds = {};
  try {
    dbIds.hotelBInt = psqlInt(`SELECT "Id" FROM "HotelBookings" WHERE "PublicId"='${seedB.hBId}';`);
    dbIds.hotelCInt = psqlInt(`SELECT "Id" FROM "HotelBookings" WHERE "PublicId"='${seedB.hCId}';`);
    psql(`UPDATE "HotelBookings" SET "Status"='Confirmado', "ConfirmedAt"=now() WHERE "Id" IN (${dbIds.hotelBInt}, ${dbIds.hotelCInt});`);
    check("Bypass SQL: hoteles B y C forzados a 'Confirmado' con la reserva en Presupuesto", true, "");
  } catch (e) {
    check("Bypass SQL: hoteles B y C forzados a 'Confirmado' con la reserva en Presupuesto", false, e.message);
    throw e;
  }

  // Reserva B: le pagamos 200 al operador imputados AL SERVICIO hotel (por API directa,
  // mismo payload que arma PagarProveedorInline — más confiable que la UI para un chequeo
  // de backend puntual como este).
  const pagoB = await apiCall("POST", `/suppliers/${seedB.supId}/payments`, {
    amount: 200, currency: "ARS", method: "Transferencia", paidAt: new Date().toISOString(),
    reference: null, notes: null, reservaId: seedB.ridB, serviceRecordKind: "hotel", servicePublicId: seedB.hBId,
  });
  check("Reserva B: pago de 200 al operador imputado al hotel (por API)", pagoB.status < 300, pagoB.text);
  if (pagoB.status >= 300) throw new Error("No se pudo pagar al operador para el seed del guard: " + pagoB.text);

  // ── El guard nuevo: PUT status InManagement sobre B debe RECHAZAR con 400 + code ──
  const putB = await apiCall("PUT", `/reservas/${seedB.ridB}/status`, { status: "InManagement" });
  console.log("CHECK_3_BODY_RESERVA_B:", JSON.stringify(putB));
  check("(3) PUT status InManagement sobre B (confirmado+pagado+sin factura) rechaza con 400",
    putB.status === 400, `status=${putB.status}`);
  check("(3) El body trae code=CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND",
    putB.json?.code === "CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND", JSON.stringify(putB.json));
  const mensajeB = putB.json?.message || "";
  textosCapturados.push(mensajeB);
  check("(3) El mensaje nombra el hotel puntual que frena la operación",
    mensajeB.includes(`Hotel P4B ${seedB.stamp}`), mensajeB);
  check("(3) El mensaje NO contiene el code ni GUIDs (nada de jerga interna)",
    !mensajeB.includes("CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND") && !GUID_RE.test(mensajeB) && !JERGA_INTERNA.test(mensajeB),
    mensajeB);

  // La reserva B tiene que seguir en Presupuesto: el rechazo es ATÓMICO, nada bajó de estado.
  const reservaBTrasRechazo = await apiCall("GET", `/reservas/${seedB.ridB}`, null);
  check("(3) Tras el rechazo: la reserva B sigue en Presupuesto (nada se aplicó a medias)",
    reservaBTrasRechazo.json?.status === "Budget", `status=${reservaBTrasRechazo.json?.status}`);

  // ── Escape: la MISMA situación (hotel Confirmado por bypass) pero SIN plata pagada
  // al operador tiene que dejar pasar el PUT — el guard bloquea por la PLATA, no por el
  // mero hecho de estar Confirmado en Presupuesto.
  const putC = await apiCall("PUT", `/reservas/${seedB.ridC}/status`, { status: "InManagement" });
  console.log("CHECK_3_BODY_RESERVA_C (escape):", JSON.stringify({ status: putC.status }));
  check("(3) Escape: reserva C (confirmado SIN pago al operador) pasa el PUT con 200",
    putC.status === 200, `status=${putC.status} body=${putC.text}`);

  // ═══════════════════════════════════════════════════════════════════════════
  // CHECK (4): eliminar con recibo anulado. El seed D ya tiene el cobro con recibo
  // Voided (armado arriba). En "Estado de Cuenta" el botón Eliminar tiene que estar
  // HABILITADO (la regla de negocio nunca bloqueó eliminar por recibo anulado — solo
  // por recibo VIGENTE o factura vinculada) y Editar gris con el motivo de auditoría.
  // ═══════════════════════════════════════════════════════════════════════════
  await page.goto(`${FRONT}/reservas/${seed.ridD}`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: /Estado de Cuenta/i }).first().click();
  await page.waitForTimeout(800);

  const btnEliminarCobro = page.locator('[data-testid="btn-eliminar-cobro"]').first();
  const btnEditarCobro = page.locator('[data-testid="btn-editar-cobro"]').first();
  await btnEliminarCobro.waitFor({ timeout: 15000 });
  await page.screenshot({ path: SHOTS + "/4-cobro-recibo-anulado.png", fullPage: true });

  check("(4) 'Eliminar cobro' está HABILITADO con recibo anulado (regla real: solo bloquea recibo vigente)",
    await btnEliminarCobro.isEnabled(), "");
  check("(4) 'Editar cobro' está DESHABILITADO (gris) con recibo anulado",
    await btnEditarCobro.isDisabled(), "");
  const motivoVisible = (await page.locator('body').innerText());
  check("(4) El motivo de auditoría está a la vista junto a los botones",
    /auditor/i.test(motivoVisible) && /recibo anulado/i.test(motivoVisible), "");

  // ── Higiene global: nada de jerga interna, GUIDs ni formato gringo en TODO lo capturado ──
  const higieneOk = textosCapturados.every((t) => !JERGA_INTERNA.test(t) && !GUID_RE.test(t) && !FORMATO_GRINGO.test(t));
  check("Higiene: ningún texto capturado tiene jerga interna, GUIDs ni formato gringo", higieneOk,
    higieneOk ? "" : textosCapturados.find((t) => JERGA_INTERNA.test(t) || GUID_RE.test(t) || FORMATO_GRINGO.test(t))?.slice(0, 200));

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
