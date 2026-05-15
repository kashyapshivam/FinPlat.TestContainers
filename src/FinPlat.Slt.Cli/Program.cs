using System.CommandLine;
using FinPlat.Slt.Cli.Commands;

// Root command for dotnet-slt CLI
var rootCommand = new RootCommand("FinPlat SLT CLI — zero-config Service Level Testing scaffolding")
{
    InitCommand.Create(),
    DoctorCommand.Create(),
    RunCommand.Create()
};

rootCommand.Description = """
    dotnet-slt: Zero-config Service Level Testing for FinPlat services.

    Commands:
      init      Auto-detect service config and scaffold SLT tests
      doctor    Verify prerequisites (Docker, .NET, etc.)
      run       Build Docker image and run SLT tests

    Quick start:
      dotnet-slt init       # Detect & scaffold
      dotnet-slt doctor     # Verify env
      dotnet-slt run        # Build & test
    """;

return await rootCommand.InvokeAsync(args);
