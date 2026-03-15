FROM node:20-slim

WORKDIR /app

# Chromium dependencies for Puppeteer (required by whatsapp-web.js)
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    fonts-ipafont-gothic \
    fonts-freefont-ttf \
    fonts-noto-color-emoji \
    libatk-bridge2.0-0 \
    libatk1.0-0 \
    libcups2 \
    libdbus-1-3 \
    libdrm2 \
    libgbm1 \
    libgtk-3-0 \
    libnspr4 \
    libnss3 \
    libx11-xcb1 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    xdg-utils \
    && rm -rf /var/lib/apt/lists/*

# Tell Puppeteer to use the installed Chromium instead of downloading its own
ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true

# Copy package files and install dependencies
COPY src/WhatsAppBot/package.json src/WhatsAppBot/package-lock.json* ./
RUN npm install --omit=dev

# Copy bot source
COPY src/WhatsAppBot/bot.js .
COPY src/WhatsAppBot/.env.example .env.example

# Session data will be persisted via volume
VOLUME ["/app/.wwebjs_auth"]

CMD ["node", "bot.js"]
