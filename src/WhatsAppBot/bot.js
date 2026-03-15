/**
 * MagnaTravel WhatsApp Bot
 * ========================
 * Bot que captura leads automáticamente desde WhatsApp
 * y los envía al CRM Pipeline del ERP via webhook.
 *
 * Requisitos:
 *   - Node.js >= 18
 *   - npm install
 *   - Copiar .env.example -> .env y configurar
 *   - Ejecutar: node bot.js
 *   - Escanear QR con el celular de la agencia
 */

require("dotenv").config();
const { Client, LocalAuth } = require("whatsapp-web.js");
const qrcode = require("qrcode-terminal");
const axios = require("axios");

// ─── Config ──────────────────────────────────────────────
const WEBHOOK_URL = process.env.WEBHOOK_URL || "http://localhost:5000/api/webhooks/whatsapp";
const WEBHOOK_SECRET = process.env.WEBHOOK_SECRET || "CHANGE_THIS_SECRET";
const SESSION_TIMEOUT = (parseInt(process.env.BOT_SESSION_TIMEOUT_MINUTES) || 30) * 60 * 1000;

// ─── Flujo Conversacional ────────────────────────────────
// Cada sesión pasa por estos pasos:
//   0 → Saludo → espera nombre
//   1 → Tiene nombre → espera destino/interés
//   2 → Tiene todo → envía al webhook → fin
const STEPS = {
    ASK_NAME: 0,
    ASK_INTEREST: 1,
    DONE: 2,
};

// Mensajes del bot (personalizables)
const MESSAGES = {
    welcome:
        process.env.BOT_WELCOME_MESSAGE ||
        "¡Hola! 👋 Gracias por contactar a *MagnaTravel* 🌎✈️\n\n¿Cuál es tu nombre completo?",
    askInterest: (name) =>
        `¡Encantado, *${name}*! 😊\n\n¿Qué destino o tipo de viaje te interesa? (ej: Cancún, Europa, Crucero, etc.)`,
    thanks: (name) =>
        `¡Perfecto, *${name}*! 🙌\n\nYa registré tu consulta. Un asesor de viajes se pondrá en contacto contigo a la brevedad.\n\n¡Gracias por elegirnos! ✨`,
    alreadyRegistered:
        "¡Hola de nuevo! 😊 Tu consulta ya fue registrada. Un asesor se pondrá en contacto contigo pronto. Si necesitás algo urgente, podés llamarnos al teléfono de la agencia.",
    error:
        "Disculpá, hubo un problema al registrar tu consulta. Por favor intentá de nuevo más tarde o llamanos directamente. 🙏",
};

// ─── Sesiones en Memoria ─────────────────────────────────
// Map<chatId, { step, name, interest, transcript[], timer, createdAt }>
const sessions = new Map();

function getOrCreateSession(chatId) {
    if (sessions.has(chatId)) {
        const session = sessions.get(chatId);
        // Reset timeout
        clearTimeout(session.timer);
        session.timer = setTimeout(() => sessions.delete(chatId), SESSION_TIMEOUT);
        return session;
    }

    const session = {
        step: STEPS.ASK_NAME,
        name: null,
        interest: null,
        transcript: [],
        createdAt: new Date(),
        timer: setTimeout(() => sessions.delete(chatId), SESSION_TIMEOUT),
    };
    sessions.set(chatId, session);
    return session;
}

// ─── Webhook ─────────────────────────────────────────────
async function sendToWebhook(phone, session) {
    const payload = {
        name: session.name,
        phone: phone,
        interest: session.interest,
        transcript: session.transcript.join("\n"),
    };

    try {
        const response = await axios.post(WEBHOOK_URL, payload, {
            headers: {
                "Content-Type": "application/json",
                "X-Webhook-Secret": WEBHOOK_SECRET,
            },
            timeout: 10000,
        });
        console.log(`✅ Lead creado: ID ${response.data?.leadId || "?"} — ${session.name} (${phone})`);
        return true;
    } catch (error) {
        const status = error.response?.status;
        const msg = error.response?.data?.message || error.message;

        if (status === 409) {
            // Ya existe un lead con ese teléfono
            console.log(`ℹ️  Lead duplicado para ${phone}: ${msg}`);
            return "duplicate";
        }

        console.error(`❌ Error enviando webhook: ${status || "?"} — ${msg}`);
        return false;
    }
}

// ─── Extraer Número de Teléfono ──────────────────────────
function extractPhone(chatId) {
    // chatId format: "5491112345678@c.us"
    const match = chatId.match(/^(\d+)@/);
    return match ? `+${match[1]}` : chatId;
}

// ─── Inicializar Cliente ─────────────────────────────────
const puppeteerConfig = {
    headless: true,
    args: [
        "--no-sandbox",
        "--disable-setuid-sandbox",
        "--disable-dev-shm-usage",
        "--disable-accelerated-2d-canvas",
        "--no-first-run",
        "--disable-gpu",
    ],
};

// En Docker, usar el Chromium del sistema (env PUPPETEER_EXECUTABLE_PATH)
if (process.env.PUPPETEER_EXECUTABLE_PATH) {
    puppeteerConfig.executablePath = process.env.PUPPETEER_EXECUTABLE_PATH;
    console.log(`🐳 Docker: usando Chromium de ${puppeteerConfig.executablePath}`);
}

const client = new Client({
    authStrategy: new LocalAuth(),
    puppeteer: puppeteerConfig,
});

// ─── Eventos ─────────────────────────────────────────────
client.on("qr", (qr) => {
    console.log("\n📱 Escaneá este código QR con WhatsApp:\n");
    qrcode.generate(qr, { small: true });
    console.log("\n(Abrí WhatsApp → Dispositivos vinculados → Vincular dispositivo)\n");
});

client.on("ready", () => {
    console.log("══════════════════════════════════════════════");
    console.log("  🤖 MagnaTravel WhatsApp Bot — CONECTADO");
    console.log("══════════════════════════════════════════════");
    console.log(`  Webhook: ${WEBHOOK_URL}`);
    console.log(`  Timeout: ${SESSION_TIMEOUT / 60000} minutos`);
    console.log("  Esperando mensajes...\n");
});

client.on("authenticated", () => {
    console.log("🔐 Sesión autenticada correctamente.");
});

client.on("auth_failure", (msg) => {
    console.error("❌ Error de autenticación:", msg);
    console.log("   Eliminá la carpeta .wwebjs_auth/ y volvé a escanear el QR.");
});

client.on("disconnected", (reason) => {
    console.log("📴 Desconectado:", reason);
    console.log("   Reiniciando en 5 segundos...");
    setTimeout(() => client.initialize(), 5000);
});

// ─── Handler Principal de Mensajes ───────────────────────
client.on("message", async (message) => {
    // Ignorar mensajes de grupos, broadcasts, y status
    if (message.from.includes("@g.us")) return;       // Grupo
    if (message.from === "status@broadcast") return;    // Estados
    if (message.fromMe) return;                         // Propios

    const chatId = message.from;
    const phone = extractPhone(chatId);
    const body = message.body?.trim();

    if (!body) return; // Ignorar mensajes vacíos (stickers, imágenes sin texto, etc.)

    console.log(`💬 [${phone}]: ${body.substring(0, 80)}${body.length > 80 ? "..." : ""}`);

    const session = getOrCreateSession(chatId);

    // Agregar al transcript
    session.transcript.push(`[Cliente]: ${body}`);

    try {
        switch (session.step) {
            // ── Paso 0: Pedir nombre ──
            case STEPS.ASK_NAME: {
                // Si es el primer mensaje, enviar bienvenida
                if (session.transcript.length === 1) {
                    const welcomeMsg = MESSAGES.welcome;
                    await message.reply(welcomeMsg);
                    session.transcript.push(`[Bot]: ${welcomeMsg}`);
                    return;
                }

                // El segundo mensaje debería ser el nombre
                session.name = body.substring(0, 200); // Limitar longitud
                session.step = STEPS.ASK_INTEREST;

                const askMsg = MESSAGES.askInterest(session.name);
                await message.reply(askMsg);
                session.transcript.push(`[Bot]: ${askMsg}`);
                break;
            }

            // ── Paso 1: Pedir interés/destino ──
            case STEPS.ASK_INTEREST: {
                session.interest = body.substring(0, 200);
                session.step = STEPS.DONE;

                // Enviar al webhook
                const result = await sendToWebhook(phone, session);

                if (result === "duplicate") {
                    const dupMsg = MESSAGES.alreadyRegistered;
                    await message.reply(dupMsg);
                    session.transcript.push(`[Bot]: ${dupMsg}`);
                } else if (result === true) {
                    const thanksMsg = MESSAGES.thanks(session.name);
                    await message.reply(thanksMsg);
                    session.transcript.push(`[Bot]: ${thanksMsg}`);
                } else {
                    const errMsg = MESSAGES.error;
                    await message.reply(errMsg);
                    session.transcript.push(`[Bot]: ${errMsg}`);
                    // Reset para que pueda reintentar
                    session.step = STEPS.ASK_NAME;
                    session.name = null;
                    session.interest = null;
                }

                // Limpiar sesión completada después de un rato
                if (session.step === STEPS.DONE) {
                    clearTimeout(session.timer);
                    session.timer = setTimeout(() => sessions.delete(chatId), 5 * 60 * 1000);
                }
                break;
            }

            // ── Ya completó el flujo ──
            case STEPS.DONE: {
                const doneMsg = MESSAGES.alreadyRegistered;
                await message.reply(doneMsg);
                break;
            }
        }
    } catch (err) {
        console.error(`❌ Error procesando mensaje de ${phone}:`, err.message);
    }
});

// ─── Arranque ────────────────────────────────────────────
console.log("🚀 Iniciando MagnaTravel WhatsApp Bot...\n");
client.initialize();

// Graceful shutdown
process.on("SIGINT", async () => {
    console.log("\n🛑 Cerrando bot...");
    await client.destroy();
    process.exit(0);
});

process.on("SIGTERM", async () => {
    await client.destroy();
    process.exit(0);
});
