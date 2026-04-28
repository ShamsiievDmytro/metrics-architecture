FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src

# Restore against full source so dotnet watch on bind-mounted source works.
COPY Forge.Metrics.sln ./
COPY Forge.Metrics.Api/Forge.Metrics.Api.csproj Forge.Metrics.Api/
COPY Forge.Metrics.Tests/Forge.Metrics.Tests.csproj Forge.Metrics.Tests/
RUN dotnet restore Forge.Metrics.sln

COPY . .

WORKDIR /src/Forge.Metrics.Api
EXPOSE 8080
CMD ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive", "--no-hot-reload"]
