# syntax=docker/dockerfile:1
#
# apiary-keeper-proxy — the nginx reverse proxy, the front door to the app in each environment (ARCHITECTURE.md D-16); prd-site and, from Phase 1, Keycloak are public on their own domains. It routes
# PROXY_APP_HOSTS to the app at PROXY_APP_UPSTREAM over the private network. Tracked by SemVer tags only, like the
# app's image: :X.Y.Z, plus the moving :staging and :production.
# VERSION and REVISION are passed by the workflows, which also set the org.opencontainers.image.source label (see the
# app's Dockerfile; a local build keeps the label its nginx base image carries); the build context is this folder.
#   docker build -t apiary-keeper-proxy:dev proxy
#   docker build --target test proxy

# nginx's own unprivileged image: runs as the nginx user (101) and listens on 8080, so no root step is needed.
FROM nginxinc/nginx-unprivileged:1.29.8-alpine AS runtime
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="apiary-keeper-proxy" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
# The resolver for the app's private address comes from the container's /etc/resolv.conf (15-local-resolvers.envsh),
# and envsubst touches only our settings, never nginx's own $variables. The staging gate is off unless an environment
# turns it on (PROXY_BASIC_AUTH; DEPLOYMENT.md §3).
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_ENVSUBST_FILTER="^(PROXY_|NGINX_LOCAL_RESOLVERS$)" \
    PROXY_MAX_BODY_SIZE=30m \
    PROXY_BASIC_AUTH=off
COPY nginx.conf /etc/nginx/nginx.conf
COPY templates/ /etc/nginx/templates/
COPY --chmod=0755 docker-entrypoint.d/05-proxy-settings.envsh /docker-entrypoint.d/
# The base image's sample server also listens on 8080. The gate's password file is written at start into a directory
# only the nginx user can read; no credential is ever in the image.
RUN rm /etc/nginx/conf.d/default.conf \
 && mkdir -m 0700 /etc/nginx/auth \
 && chown 101:101 /etc/nginx/auth
USER 101
EXPOSE 8080 9113

# The configuration tests (tests/config-test.sh): `docker build --target test proxy`
FROM runtime AS test
COPY --chmod=0755 tests/config-test.sh /tmp/config-test.sh
RUN /tmp/config-test.sh
