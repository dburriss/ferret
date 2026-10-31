#!/bin/bash

# Wrapper script for the F# install-alpha script.
#
# Arguments:
#   --dry-run    Simulate without building or installing.
#
# Usage:
#   ./install-alpha.sh
#   ./install-alpha.sh --dry-run

dotnet fsi scripts/install-alpha.fsx \
  --fsproj "src/Ferret.Cli/Ferret.Cli.fsproj" \
  --solution "Ferret.slnx" \
  --package-id "Ferret.Tool" \
  "$@"
