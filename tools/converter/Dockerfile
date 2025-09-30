FROM debian:12-slim
ENV DEBIAN_FRONTEND=noninteractive

# LibreOffice headless + fonts (including Trebuchet MS) + python + Flask/Waitress from Debian repos
RUN set -eux; \
  . /etc/os-release; \
  printf 'deb http://deb.debian.org/debian %s main contrib non-free non-free-firmware\n' "$VERSION_CODENAME" > /etc/apt/sources.list; \
  printf 'deb http://deb.debian.org/debian %s-updates main contrib non-free non-free-firmware\n' "$VERSION_CODENAME" >> /etc/apt/sources.list; \
  printf 'deb http://security.debian.org/debian-security %s-security main contrib non-free non-free-firmware\n' "$VERSION_CODENAME" >> /etc/apt/sources.list; \
  apt-get update; \
  echo "ttf-mscorefonts-installer msttcorefonts/accepted-mscorefonts-eula select true" | debconf-set-selections; \
  apt-get install -y --no-install-recommends \
    cabextract fontconfig wget \
    libreoffice-writer libreoffice-impress libreoffice-calc libreoffice-draw libreoffice-core \
    fonts-dejavu fonts-liberation ttf-mscorefonts-installer \
    python3 python3-venv python3-pip python3-flask python3-waitress ca-certificates \
    poppler-utils python3-pypdf \
  ; \
  fc-cache -f; \
  rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY server.py /app/server.py

# Temp workspace if you want to mount/persist later
RUN mkdir -p /work

EXPOSE 5019

# Keep LO warm, then run Flask app via Waitress
CMD bash -lc "\
  soffice --headless --nologo --nofirststartwizard \
    --accept='socket,host=127.0.0.1,port=2002;urp;' & \
  exec python3 -m waitress --host=0.0.0.0 --port=5019 --call server:create_app"
