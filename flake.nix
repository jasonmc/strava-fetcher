{
  description = "Minimal F# CLI for fetching and normalizing Strava ride data";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
    flake-utils.url = "github:numtide/flake-utils";
    nuget-packageslock2nix = {
      url = "github:mdarocha/nuget-packageslock2nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs = { self, nixpkgs, flake-utils, nuget-packageslock2nix }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
        dotnetSdk = pkgs.dotnetCorePackages.sdk_10_0-bin;
        dotnetRuntime = pkgs.dotnetCorePackages.runtime_10_0-bin;
      in
      {
        packages.default = pkgs.buildDotnetModule {
          pname = "strava-fetcher";
          version = "0.1.0";
          src = ./.;
          projectFile = "src/StravaFetcher.Cli/StravaFetcher.Cli.fsproj";
          nugetDeps = nuget-packageslock2nix.lib {
            inherit system;
            name = "strava-fetcher";
            lockfiles = [
              ./src/StravaFetcher/packages.lock.json
              ./src/StravaFetcher.Cli/packages.lock.json
              ./tests/StravaFetcher.Tests/packages.lock.json
            ];
            excludePackages = [
              "FSharp.Core-10.0.103"
            ];
          };
          dotnet-sdk = dotnetSdk;
          dotnet-runtime = dotnetRuntime;
          executables = [ "StravaFetcher.Cli" ];
          selfContainedBuild = false;
          testProjectFile = "tests/StravaFetcher.Tests/StravaFetcher.Tests.fsproj";
        };

        checks.default = self.packages.${system}.default;

        apps.default = {
          type = "app";
          program = "${self.packages.${system}.default}/bin/StravaFetcher.Cli";
        };

        devShells.default = pkgs.mkShell {
          packages = [
            dotnetSdk
            pkgs.git
            pkgs.gh
            pkgs.jq
          ];
        };
      });
}
