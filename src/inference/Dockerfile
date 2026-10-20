# CPU inference image. Build context: repository root.
#   docker build -f src/inference/Dockerfile -t imageto3d-inference .
# Models are NOT baked in (3.5 GB); mount ./models read-only at /models.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/shared/ImageTo3D.Core/ImageTo3D.Core.csproj src/shared/ImageTo3D.Core/
COPY src/inference/ImageTo3D.Inference/ImageTo3D.Inference.csproj src/inference/ImageTo3D.Inference/
RUN dotnet restore src/inference/ImageTo3D.Inference/ImageTo3D.Inference.csproj
COPY src/shared src/shared
COPY src/inference/ImageTo3D.Inference src/inference/ImageTo3D.Inference
RUN dotnet publish src/inference/ImageTo3D.Inference/ImageTo3D.Inference.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Inference__ModelDirectory=/models \
    Inference__WorkDirectory=/data/inference
RUN mkdir -p /data/inference && chown -R app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ImageTo3D.Inference.dll"]
