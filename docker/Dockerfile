FROM public.ecr.aws/docker/library/node:24-bookworm-slim AS web-build
WORKDIR /src
RUN corepack enable
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY src/CloudEmuera.Web/package.json src/CloudEmuera.Web/package.json
RUN corepack pnpm install --frozen-lockfile
COPY src/CloudEmuera.Web src/CloudEmuera.Web
RUN corepack pnpm --dir src/CloudEmuera.Web build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props CloudEmuera.slnx ./
COPY src ./src
COPY tests ./tests
COPY assets ./assets
COPY --from=web-build /src/src/CloudEmuera.Web/dist src/CloudEmuera.Api/wwwroot
RUN dotnet restore CloudEmuera.slnx --locked-mode
RUN dotnet publish src/CloudEmuera.Api/CloudEmuera.Api.csproj --no-restore -c Release -o /out/api
RUN dotnet publish src/CloudEmuera.Worker/CloudEmuera.Worker.csproj --no-restore -c Release -o /out/worker
RUN dotnet publish src/CloudEmuera.Validator/CloudEmuera.Validator.csproj --no-restore -c Release -o /out/validator
RUN dotnet publish src/CloudEmuera.Migrator/CloudEmuera.Migrator.csproj --no-restore -c Release -o /out/migrator

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && \
    apt-get install -y --no-install-recommends libgdiplus && \
    ln -sf /usr/lib/libgdiplus.so /usr/lib/libgdiplus.dll.so && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app/api
ENV ASPNETCORE_URLS=http://0.0.0.0:28647 \
    CloudEmuera__DataPath=/data \
    CloudEmuera__WorkerAssemblyPath=/app/worker/CloudEmuera.Worker.dll \
    CloudEmuera__ValidatorAssembly=/app/validator/CloudEmuera.Validator.dll \
    CloudEmuera__RuntimeFontRoot=/app/runtime-fonts
COPY --from=dotnet-build /out/api /app/api/
COPY --from=dotnet-build /out/worker /app/worker/
COPY --from=dotnet-build /out/validator /app/validator/
COPY --from=dotnet-build /out/migrator /app/migrator/
COPY --from=dotnet-build /src/assets/runtime-fonts /app/runtime-fonts/
COPY docker/start.sh /app/start.sh
RUN mkdir -p /data && \
    chown -R root:root /app && \
    chmod -R a=rX /app && \
    chmod 0555 /app/start.sh && \
    chmod 0770 /data
EXPOSE 28647
STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/start.sh"]
