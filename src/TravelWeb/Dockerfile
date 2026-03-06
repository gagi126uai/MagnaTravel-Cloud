FROM node:20-alpine AS build
WORKDIR /app
ARG VITE_API_URL
ENV VITE_API_URL=${VITE_API_URL}

# Build context is repo root, so paths start with src/TravelWeb
COPY src/TravelWeb/package.json src/TravelWeb/package-lock.json* ./
RUN npm install

COPY src/TravelWeb/ .
RUN npm run build

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html
COPY --from=build /app/dist .
COPY src/TravelWeb/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
