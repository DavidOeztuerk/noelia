FROM nginx:1.29-alpine

# Local self-signed certificate covering app and operator host names.
# Generated in this build step (Docker may reuse the cached layer); never use
# this image-carried demo key as a production TLS credential.
RUN apk add --no-cache openssl \
    && mkdir -p /etc/nginx/tls \
    && openssl req -x509 -nodes -newkey rsa:2048 -days 365 \
        -keyout /etc/nginx/tls/demo.key \
        -out /etc/nginx/tls/demo.crt \
        -subj "/CN=noelia-demo.localhost" \
        -addext "subjectAltName=DNS:micro-dev.localhost,DNS:mono-dev.localhost,DNS:micro-staging.localhost,DNS:mono-staging.localhost,DNS:micro-prod.localhost,DNS:mono-prod.localhost,DNS:gateway-staging.localhost,DNS:gateway-prod.localhost,DNS:user-staging.localhost,DNS:todo-staging.localhost,DNS:user-prod.localhost,DNS:todo-prod.localhost,DNS:localhost,IP:127.0.0.1" \
    && chmod 600 /etc/nginx/tls/demo.key \
    && apk del openssl

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY snippets /etc/nginx/snippets
COPY index.html login.html register.html noelia.html favicon.png /usr/share/nginx/html/
COPY css /usr/share/nginx/html/css
COPY js /usr/share/nginx/html/js

EXPOSE 8080 8443
