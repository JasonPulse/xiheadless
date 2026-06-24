# XiHeadless FFXI bot — console app (.NET 9). Configured at runtime by 3 env vars
# (XIBOT_ACCOUNT, XIBOT_PASSWORD, XIBOT_BRAIN); see Program.cs. Navmeshes are mounted
# separately (XIBOT_NAVMESH_DIR), not baked into the image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish XiHeadless.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8088
ENTRYPOINT ["dotnet", "XiHeadless.dll"]
