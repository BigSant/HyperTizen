FROM debian:bookworm-slim

ARG HYPERHDR_VERSION=21.0.0.0
ARG HYPERHDR_SHA256=2fdf19bd176a262188f5bc5a842fafe396687eca7156c96d6da82c79072b5fae
ARG HYPERHDR_DEB=HyperHDR-21.0.0.0.bookworm-x86_64.deb

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       ca-certificates curl ffmpeg intel-media-va-driver libglib2.0-0 \
       libva2 python3 python3-pip supervisor vainfo xz-utils \
    && curl -fsSL -o /tmp/hyperhdr.deb \
       "https://github.com/awawa-dev/HyperHDR/releases/download/v${HYPERHDR_VERSION}/${HYPERHDR_DEB}" \
    && echo "${HYPERHDR_SHA256}  /tmp/hyperhdr.deb" | sha256sum -c - \
    && apt-get install -y --no-install-recommends /tmp/hyperhdr.deb \
    && rm -f /tmp/hyperhdr.deb \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 568 --user-group --home-dir /config --shell /usr/sbin/nologin hyperhdr

WORKDIR /opt/hypertizen
COPY tools/requirements.txt tools/source_bridge.py tools/source_bridge_control.py ./
COPY controls.html tools/server_logs.html ./
RUN pip3 install --break-system-packages --no-cache-dir -r requirements.txt
COPY deploy/truenas-hyperhdr/supervisord.conf /etc/supervisor/conf.d/hypertizen.conf

ENV QT_QPA_PLATFORM=offscreen
EXPOSE 8090 8092 19400 19444 19445
VOLUME ["/config", "/state", "/media"]
ENTRYPOINT ["/usr/bin/supervisord", "-n", "-c", "/etc/supervisor/supervisord.conf"]
