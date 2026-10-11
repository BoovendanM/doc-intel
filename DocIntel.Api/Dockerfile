# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached between code changes
COPY DocIntel.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Pre-create the upload folder owned by the non-root 'app' user so the
# DocIntel_FileShare named volume inherits writable permissions on first mount.
RUN mkdir -p /app/uploads && chown $APP_UID /app/uploads

COPY --from=build /app/publish ./

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocIntel.Api.dll"]

