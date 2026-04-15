# build stage
FROM node:18-bullseye AS build
WORKDIR /app
COPY package.json package-lock.json* ./
# Use --legacy-peer-deps to avoid peer dependency resolution failures inside the container
# and fall back to npm install if ci is not possible. This is a pragmatic dev fix; for
# reproducible builds consider generating and checking in package-lock.json from your dev host.
RUN npm ci --silent --no-audit --no-fund --legacy-peer-deps || npm install --legacy-peer-deps
COPY . .
RUN npm run build

# runtime stage - serve with nginx
FROM nginx:stable-alpine
COPY --from=build /app/dist /usr/share/nginx/html
# optional: provide a basic nginx config to allow SPA routing
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
