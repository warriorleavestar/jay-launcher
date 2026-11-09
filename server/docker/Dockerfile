# Jay 网络服务端（公共网络节点）的容器镜像，从源码构建。
#   docker build -f server/docker/Dockerfile -t jay-network .
# 用法见 server/docker/docker-compose.yml 和 docs/network-server.md。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/Amber.Server -c Release -o /out -p:DebugType=none -p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10010 --home /data jay && mkdir -p /data && chown jay /data
COPY --from=build /out /opt/jay-server
USER jay
ENV JAY_SERVER_DIR=/data DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /data
EXPOSE 11010/tcp 11010/udp
# 管理接口只听容器里的 127.0.0.1，健康检查在容器里面跑。
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD curl -fsS http://127.0.0.1:47669/health || exit 1
ENTRYPOINT ["/opt/jay-server/jay-server"]
CMD ["directory", "--listen", "11010"]
