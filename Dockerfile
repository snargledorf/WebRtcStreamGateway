FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source

COPY src/WebRtcStreamGateway/WebRtcStreamGateway.csproj src/WebRtcStreamGateway/
RUN dotnet restore src/WebRtcStreamGateway/WebRtcStreamGateway.csproj

COPY src/WebRtcStreamGateway/ src/WebRtcStreamGateway/
WORKDIR /source/src/WebRtcStreamGateway
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

ENTRYPOINT ["dotnet", "WebRtcStreamGateway.dll"]
