FROM nginx:1.29-alpine

# One certificate covering the six demo host names. Generated at build time so
# the image carries no key anyone else has ever held, and regenerated on every
# rebuild — a demo certificate that outlives the demo is a key nobody is
# tracking.
RUN apk add --no-cache openssl \
    && mkdir -p /etc/nginx/tls \
    && openssl req -x509 -nodes -newkey rsa:2048 -days 365 \
        -keyout /etc/nginx/tls/demo.key \
        -out /etc/nginx/tls/demo.crt \
        -subj "/CN=noelia-demo.localhost" \
        -addext "subjectAltName=DNS:micro-dev.localhost,DNS:mono-dev.localhost,DNS:micro-staging.localhost,DNS:mono-staging.localhost,DNS:micro-prod.localhost,DNS:mono-prod.localhost,DNS:localhost,IP:127.0.0.1" \
    && chmod 600 /etc/nginx/tls/demo.key \
    && apk del openssl

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY snippets /etc/nginx/snippets
COPY index.html login.html register.html favicon.png /usr/share/nginx/html/
COPY css /usr/share/nginx/html/css
COPY js /usr/share/nginx/html/js

EXPOSE 8080 8443
