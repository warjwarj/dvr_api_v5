#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app

# expose the ports that we need for the servers
EXPOSE 9046 9047 9048

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["dvr_api.csproj", "."]
RUN dotnet restore "./dvr_api.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "dvr_api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "dvr_api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "dvr_api.dll"]