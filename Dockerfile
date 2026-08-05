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
COPY --from=web-build /src/src/CloudEmuera.Web/dist src/CloudEmuera.Api/wwwroot
RUN dotnet restore CloudEmuera.slnx --locked-mode
RUN dotnet publish src/CloudEmuera.Api/CloudEmuera.Api.csproj --no-restore -c Release -o /out/api

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:18080 \
    CloudEmuera__DataPath=/data
COPY --from=dotnet-build /out/api ./
RUN mkdir -p /data && chown -R app:app /data /app
USER app
EXPOSE 18080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "CloudEmuera.Api.dll"]
