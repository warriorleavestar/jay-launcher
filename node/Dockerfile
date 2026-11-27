# Jay relay node image: Jay's hardened EasyTier build (the same pinned release as install.sh, SHA-256 checked) on a
# base image pinned by digest. The EasyTier hashes are build/pins.json's easytier-linux-x86_64 / easytier-linux-aarch64;
# change them there first. Built locally by docker-compose.yml; nothing is pulled by a movable tag.
ARG BASE=alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf

FROM ${BASE} AS fetch
ARG TARGETARCH
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
        amd64) arch=x86_64; sha=6ad63ed07f270f2942387d1257ef0db45a31b2e525c73f8aa981c7c94fbcb300 ;; \
        arm64) arch=aarch64; sha=6c1a99ea81f1e1dd8fc8f13aa6f0df8d9e22925202dda18b4e40ad62ac49f7ac ;; \
        *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    wget -q -O /tmp/easytier.zip "https://github.com/Yumao-Feather/jay-easytier/releases/download/jay-v2.6.4-3/easytier-linux-${arch}-v2.6.4-jay-9407746b1ce1.zip"; \
    echo "${sha}  /tmp/easytier.zip" | sha256sum -c -; \
    mkdir /tmp/x; \
    unzip -q /tmp/easytier.zip -d /tmp/x; \
    core="$(find /tmp/x -type f -name easytier-core | head -n 1)"; \
    [ -n "$core" ]; \
    install -m 0755 "$core" /easytier-core

FROM ${BASE}
COPY --from=fetch /easytier-core /usr/local/bin/easytier-core
# Not root inside the container either (the compose file also drops every capability).
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/easytier-core"]
