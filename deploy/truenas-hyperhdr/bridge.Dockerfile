FROM python:3.12-slim-bookworm

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       ffmpeg intel-media-va-driver libva2 vainfo \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/hypertizen
COPY tools/requirements.txt tools/source_bridge.py tools/source_bridge_control.py ./
RUN pip install --no-cache-dir -r requirements.txt

EXPOSE 19445
ENTRYPOINT ["python3", "/opt/hypertizen/source_bridge_control.py"]
CMD ["--listen", "0.0.0.0"]
