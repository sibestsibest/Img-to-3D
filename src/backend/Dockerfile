# Build context: repository root.
#   docker build -f src/backend/Dockerfile -t imageto3d-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/shared/ImageTo3D.Core/ImageTo3D.Core.csproj src/shared/ImageTo3D.Core/
COPY src/backend/ImageTo3D.Api/ImageTo3D.Api.csproj src/backend/ImageTo3D.Api/
RUN dotnet restore src/backend/ImageTo3D.Api/ImageTo3D.Api.csproj
COPY src/shared src/shared
COPY src/backend/ImageTo3D.Api src/backend/ImageTo3D.Api
RUN dotnet publish src/backend/ImageTo3D.Api/ImageTo3D.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Storage__RootPath=/data/storage \
    Storage__DatabasePath=/data/imageto3d.db
RUN mkdir -p /data/storage && chown -R app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ImageTo3D.Api.dll"]
