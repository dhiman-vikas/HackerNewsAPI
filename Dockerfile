# syntax=docker/dockerfile:1

# ---- build stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached independently of source edits.
COPY Directory.Build.props .editorconfig ./
COPY RestfulAPIDemo/RestfulAPIDemo.csproj RestfulAPIDemo/
RUN dotnet restore RestfulAPIDemo/RestfulAPIDemo.csproj

COPY RestfulAPIDemo/ RestfulAPIDemo/
RUN dotnet publish RestfulAPIDemo/RestfulAPIDemo.csproj -c Release -o /app --no-restore

# ---- runtime stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Plain HTTP inside the container; TLS is terminated by the ingress / reverse proxy.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the non-root user that the base image provides.
USER $APP_UID
ENTRYPOINT ["dotnet", "RestfulAPIDemo.dll"]
