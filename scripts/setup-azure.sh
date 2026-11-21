#!/usr/bin/env bash
# Sets up the CI identity for a subscription, once, by its owner (infra/README.md, "Setting up a subscription").
# Run from your own terminal, signed in with `az login` as an Owner of the subscription and a tenant admin, and
# with `gh auth login` on the repository. Safe to run again: it only adds what is missing.
#
# For each GitHub environment (production, staging) it makes the app registration `workstream-github-<env>`
# and its service principal, and gives the principal, at subscription scope:
#   - Contributor: every resource the Bicep declares, whatever types it adds later, and the builder machine
#     the desktops' image is baked on;
#   - Role Based Access Control Administrator, conditioned to assign only the four roles the Bicep gives the
#     runtime identity: AcrPull (registry-access.bicep), Virtual Machine Contributor (desktops.bicep,
#     desktop-access.bicep), Disk Snapshot Contributor (desktops.bicep, for environments' saved setups) and
#     Reader (desktop-access.bicep);
#   - Microsoft Graph Group.ReadWrite.All, for each copy's `…-sql-admins` group (data.bicep).
# It stores the principal's credential as the environment's AZURE_CREDENTIALS secret when the environment has none.
#
# Nothing here depends on what the Bicep deploys, so a change to infra never needs this run again, except a
# new role for the runtime identity: the condition is then written again, which only an Owner can do.
#
#   PREFIX  app registration name prefix (default workstream-github)
#   ENVS    environments to set up (default "production staging")
#   REPO    GitHub repository for the secrets (default agenetix/workstream); "none" stores no secret
set -euo pipefail

PREFIX=${PREFIX:-workstream-github}
ENVS=${ENVS:-production staging}
REPO=${REPO:-agenetix/workstream}

subscription=$(az account show --query id -o tsv)
tenant=$(az account show --query tenantId -o tsv)
scope="/subscriptions/$subscription"
echo "Subscription $(az account show --query name -o tsv) ($subscription)"

contributor=b24988ac-6180-42a0-ab88-20f7382dd24c
rbac_admin=f58310d9-a9f6-439a-9e8d-f62e7b41a168
acr_pull=7f951dda-4ed3-4680-a7ca-43fe172d538d
machine_contributor=9980e02c-c2be-4d73-94e8-173b1dc7cf3c
reader=acdd72a7-3385-48ef-bd42-f606fba81ae7
snapshot_contributor=7efff54f-a5b4-42b5-a1c5-5411624893ce
graph_group_readwrite=62a82d76-70ea-41e2-9197-370581804d09
roles="$acr_pull, $machine_contributor, $snapshot_contributor, $reader"
condition="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roles})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roles}))"

graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)

assign() { # principal, role id, [condition]
  local existing
  existing=$(az role assignment list --scope "$scope" --query "[?principalId=='$1' && ends_with(roleDefinitionId, '$2')].id" -o tsv)
  if [ -n "$existing" ]; then
    # Already there with the same condition. One with another condition (a role was added) is made again.
    [ -z "${3:-}" ] || [ "$(az role assignment list --scope "$scope" --query "[?principalId=='$1' && ends_with(roleDefinitionId, '$2')].condition | [0]" -o tsv)" = "$3" ] && return
    az role assignment delete --ids $existing -o none
  fi
  if [ -n "${3:-}" ]; then
    az role assignment create --assignee-object-id "$1" --assignee-principal-type ServicePrincipal --role "$scope/providers/Microsoft.Authorization/roleDefinitions/$2" --scope "$scope" \
      --condition "$3" --condition-version 2.0 -o none
  else
    az role assignment create --assignee-object-id "$1" --assignee-principal-type ServicePrincipal --role "$scope/providers/Microsoft.Authorization/roleDefinitions/$2" --scope "$scope" -o none
  fi
}

for env in $ENVS; do
  name="$PREFIX-$env"
  app=$(az ad app list --display-name "$name" --query "[0].appId" -o tsv)
  [ -n "$app" ] || app=$(az ad app create --display-name "$name" --query appId -o tsv)
  sp=$(az ad sp list --filter "appId eq '$app'" --query "[0].id" -o tsv)
  [ -n "$sp" ] || sp=$(az ad sp create --id "$app" --query id -o tsv)

  # A new principal can take a little while to reach Azure Resource Manager.
  for attempt in 1 2 3 4 5 6; do
    assign "$sp" "$contributor" && break
    [ "$attempt" = 6 ] && exit 1
    sleep 10
  done
  assign "$sp" "$rbac_admin" "$condition"

  if [ -z "$(az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignments" \
      --query "value[?appRoleId=='$graph_group_readwrite'].id" -o tsv)" ]; then
    az rest --method post --url "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignments" \
      --body "{\"principalId\":\"$sp\",\"resourceId\":\"$graph\",\"appRoleId\":\"$graph_group_readwrite\"}" -o none
  fi

  if [ "$REPO" != none ] && ! gh secret list -R "$REPO" --env "$env" --json name --jq '.[].name' | grep -qx AZURE_CREDENTIALS; then
    az ad app credential reset --id "$app" --append --display-name "Workstream GitHub CI" --years 1 -o json \
      | jq -c --arg c "$app" --arg t "$tenant" --arg s "$subscription" '{clientId:$c, clientSecret:.password, tenantId:$t, subscriptionId:$s}' \
      | gh secret set AZURE_CREDENTIALS -R "$REPO" --env "$env"
  fi
  echo "$name: Contributor, Role Based Access Control Administrator (AcrPull, Virtual Machine Contributor, Reader), Graph Group.ReadWrite.All"
done
