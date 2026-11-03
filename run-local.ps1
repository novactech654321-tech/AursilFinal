$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet run --project ".\AurSil.Api\AurSil.Api.csproj" --urls "http://localhost:5010"
