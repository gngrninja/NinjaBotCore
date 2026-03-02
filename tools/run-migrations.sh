#!/bin/bash
# Run EF Core migrations on the production host.
# Called by deploy.sh after syncing code, before restarting containers.
# Can also be run manually: bash tools/run-migrations.sh
set -e

export PATH="$HOME/.dotnet/tools:$PATH"

SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

# Load environment variables, handling values with spaces and = signs
while IFS='=' read -r key value; do
  [[ -z "$key" || "$key" == \#* ]] && continue
  export "$key=$value"
done < .env.production

dotnet ef database update --project src/NinjaBotCore.csproj
