FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update -qq \
    && apt-get install -y --no-install-recommends ca-certificates curl git jq docker.io iptables python3 unzip \
    && curl -fsSL https://deb.nodesource.com/setup_22.x -o /tmp/node-setup.sh \
    && bash /tmp/node-setup.sh \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://astral.sh/uv/install.sh -o /tmp/uv-install.sh \
    && sh /tmp/uv-install.sh \
    && /root/.local/bin/uv python install 3.13

ENV PATH="/root/.aspire/bin:/root/.local/bin:${PATH}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    ASPIRE_CLI_TELEMETRY_OPTOUT=1 \
    ASPIRE_CLI_START_TIMEOUT=300 \
    TERM=xterm-256color

COPY entrypoint.sh install-cli build-template validate-output collect-diagnostics /usr/local/bin/
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/install-cli /usr/local/bin/build-template \
    /usr/local/bin/validate-output /usr/local/bin/collect-diagnostics
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
