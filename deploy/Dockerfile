# Stage 1: Build React/Vite/TypeScript client
FROM node:24-alpine AS client-build
WORKDIR /src/Nexus.Client
COPY src/Nexus.Client/package.json src/Nexus.Client/package-lock.json ./
RUN npm ci
COPY src/Nexus.Client/ ./
RUN npm run build

# Stage 2: Build and publish ASP.NET Core host
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig Nexus.slnx ./
COPY src/Nexus.Web/Nexus.Web.csproj src/Nexus.Web/
RUN dotnet restore src/Nexus.Web/Nexus.Web.csproj
COPY src/Nexus.Web/ src/Nexus.Web/
RUN dotnet publish src/Nexus.Web/Nexus.Web.csproj -c Release -o /app/publish --no-restore /p:SkipClientPublish=true

# Stage 3: Minimal non-root runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=server-build /app/publish ./
COPY --from=client-build /src/Nexus.Client/dist ./wwwroot
RUN mkdir -p /app/App_Data/storage && chown -R app:app /app

USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Nexus.Web.dll"]
