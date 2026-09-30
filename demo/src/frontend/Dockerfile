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

# A template, not a finished config: the nginx image renders every
# /etc/nginx/templates/*.template into conf.d at start, and the one thing that
# has to be known only then is the HTTPS port the *outside* sees (the http->https
# redirect must name it; 8443 inside the container says nothing about it). The
# filter keeps envsubst away from every variable that is not ours, so nginx's
# own $host, $uri and $request_uri survive untouched.
COPY nginx.conf /etc/nginx/templates/default.conf.template
ENV NGINX_ENVSUBST_FILTER=^NOELIA_ \
    NOELIA_EXTERNAL_HTTPS_PORT=8443
COPY snippets /etc/nginx/snippets
COPY index.html login.html register.html noelia.html favicon.png /usr/share/nginx/html/
COPY css /usr/share/nginx/html/css
COPY js /usr/share/nginx/html/js

EXPOSE 8080 8443
