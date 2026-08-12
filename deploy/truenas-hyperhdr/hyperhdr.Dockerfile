FROM debian:bookworm-slim

ARG HYPERHDR_VERSION=21.0.0.0
ARG HYPERHDR_SHA256=2fdf19bd176a262188f5bc5a842fafe396687eca7156c96d6da82c79072b5fae
ARG HYPERHDR_DEB=HyperHDR-21.0.0.0.bookworm-x86_64.deb

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl libglib2.0-0 xz-utils \
    && curl -fsSL -o /tmp/hyperhdr.deb \
       "https://github.com/awawa-dev/HyperHDR/releases/download/v${HYPERHDR_VERSION}/${HYPERHDR_DEB}" \
    && echo "${HYPERHDR_SHA256}  /tmp/hyperhdr.deb" | sha256sum -c - \
    && apt-get install -y --no-install-recommends /tmp/hyperhdr.deb \
    && rm -f /tmp/hyperhdr.deb \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 568 --user-group --home-dir /config --shell /usr/sbin/nologin hyperhdr

ENV QT_QPA_PLATFORM=offscreen
EXPOSE 8090 8092 19400 19444 19445
VOLUME ["/config"]
USER 568:568
ENTRYPOINT ["hyperhdr"]
CMD ["--userdata", "/config", "--service"]
