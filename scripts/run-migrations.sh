#!/usr/bin/env sh
# Starts the migrator Container Apps job and waits for it, so a failed migration fails the deploy.
# Runs as the azd postdeploy hook (see azure.yaml); azd supplies AZURE_ENV_NAME and AZURE_SUBSCRIPTION_ID.
# Requires the Azure CLI signed in to the same subscription as azd (az login is separate from azd auth login).
# Overrides: RESOURCE_GROUP, JOB_NAME, TIMEOUT_SECONDS, POLL_SECONDS.

set -eu

resource_group="${RESOURCE_GROUP:-${AZURE_RESOURCE_GROUP:-rg-${AZURE_ENV_NAME}}}"
subscription="${AZURE_SUBSCRIPTION_ID:-}"
job_name="${JOB_NAME:-migrator}"
# A little over the job's replicaTimeout (1800 s), after which Container Apps fails it anyway
timeout_seconds="${TIMEOUT_SECONDS:-1900}"
poll_seconds="${POLL_SECONDS:-10}"

set -- -n "$job_name" -g "$resource_group"
if [ -n "$subscription" ]; then set -- "$@" --subscription "$subscription"; fi

execution=$(az containerapp job start "$@" --query name -o tsv)
echo "Started $job_name execution $execution in $resource_group"

deadline=$(( $(date +%s) + timeout_seconds ))
while :; do
    sleep "$poll_seconds"
    status=$(az containerapp job execution show "$@" --job-execution-name "$execution" --query properties.status -o tsv)
    echo "  $status"
    case "$status" in
        Running|Processing|Unknown) [ "$(date +%s)" -lt "$deadline" ] || break ;;
        *) break ;;
    esac
done

if [ "$status" != "Succeeded" ]; then
    # Logs are under the job's Execution history in the portal (az containerapp job logs needs the containerapp extension)
    echo "Migration $execution in $resource_group ended as '$status'" >&2
    exit 1
fi
echo "Migrations applied"
