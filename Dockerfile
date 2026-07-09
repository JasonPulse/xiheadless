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
# CONTAINER MEMORY (fleet OOM incident, 2026-07-09): the bot's real working set is ~230Mi (measured: full
# login->travel->AH->combat flow peaked 232Mi under a 768Mi limit), but under a 384Mi cgroup the default GC
# (heap budget = 75% of the limit) lets allocation spikes overshoot the remaining headroom before collecting
# -> the KERNEL kills the cgroup (13/13 played wave pods OOMKilled in <60s, both archs; not a leak).
# Cap the managed heap well under the cgroup so the GC collects instead of the kernel killing:
ENV DOTNET_GCHeapHardLimitPercent=55 \
    DOTNET_GCConserveMemory=5
EXPOSE 8088
ENTRYPOINT ["dotnet", "XiHeadless.dll"]
