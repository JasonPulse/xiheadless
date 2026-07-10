# XiHeadless FFXI bot — console app (.NET 9). Configured at runtime by 3 env vars
# (XIBOT_ACCOUNT, XIBOT_PASSWORD, XIBOT_BRAIN); see Program.cs. Navmeshes are mounted
# separately (XIBOT_NAVMESH_DIR), not baked into the image.
# Multi-arch (amd64 + arm64): the SDK stage always runs on the BUILD host's arch and CROSS-COMPILES for
# the target (-a $TARGETARCH) — the bot is pure managed code (zero native libs), so no per-arch toolchain
# and no slow QEMU-emulated compile. The runtime stage resolves to the target platform automatically.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish XiHeadless.csproj -c Release -a $TARGETARCH -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app ./
# CONTAINER MEMORY: the GC caps live in XiHeadless.csproj as RuntimeHostConfigurationOptions (decimal),
# NOT here as DOTNET_GC* env — those parse as HEX ("55" = 85%!), a trap that made the first fix worse.
EXPOSE 8088
ENTRYPOINT ["dotnet", "XiHeadless.dll"]
