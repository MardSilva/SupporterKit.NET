FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY . .
RUN dotnet restore SupporterKit.NET.slnx --locked-mode
RUN dotnet publish src/Supporter.Api/Supporter.Api.csproj -c Release -o /out --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
LABEL org.opencontainers.image.version="0.1.1"
WORKDIR /app
RUN mkdir -p /data && chown app:app /data
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Supporter__DataDirectory=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Supporter.Api.dll"]
