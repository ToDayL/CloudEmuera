#!/usr/bin/env bash

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  echo "scripts/lib/dev-env.sh must be sourced" >&2
  exit 1
fi

export CLOUDEMUERA_UID="${CLOUDEMUERA_UID:-$(id -u)}"
export CLOUDEMUERA_GID="${CLOUDEMUERA_GID:-$(id -g)}"

