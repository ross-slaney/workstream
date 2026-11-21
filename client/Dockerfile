# The web app: Expo's server output (every page rendered per request, plus the API routes) behind server.js.
# The bundle is exported first, outside Docker, so Metro's cache survives between builds
# (scripts/export-web.sh), then packaged here with client/ as the context:
#   EXPO_PUBLIC_API_URL=https://api.example.com npm run export:web
#   docker build -f Dockerfile --build-arg EXPO_PUBLIC_API_URL=https://api.example.com .
# The API's origin is inlined into the bundle at export time, so each environment exports its own. The
# runtime needs only the server adapter and Express: the exported server bundle carries the app and every
# dependency it renders with.
FROM node:22-bookworm-slim
ARG EXPO_PUBLIC_API_URL
ARG EXPO_PUBLIC_SQLOS_CLIENT_ID=workstream-app
# The client bundle has these inlined; the server bundle reads them at request time.
ENV NODE_ENV=production PORT=8080 \
    EXPO_PUBLIC_API_URL=$EXPO_PUBLIC_API_URL \
    EXPO_PUBLIC_SQLOS_CLIENT_ID=$EXPO_PUBLIC_SQLOS_CLIENT_ID
WORKDIR /app
COPY server/package.json server/package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund
COPY server.js ./
COPY dist ./dist
USER node
EXPOSE 8080
CMD ["node", "server.js"]
