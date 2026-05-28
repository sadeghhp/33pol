#!/usr/bin/env bash
# Git clone and repo root detection.

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

install_find_repo_root() {
  local dir="$1"
  while [[ "${dir}" != "/" ]]; do
    if [[ -f "${dir}/33pol.sln" && -f "${dir}/docker-compose.yml" ]]; then
      printf '%s' "${dir}"
      return 0
    fi
    dir="$(dirname "${dir}")"
  done
  return 1
}

install_clone_or_update() {
  local git_url="$1"
  local install_dir="$2"
  local git_ref="$3"

  if [[ "${INSTALL_DRY_RUN:-false}" == true ]]; then
    log "[dry-run] would clone ${git_url} -> ${install_dir} (ref: ${git_ref:-default})"
    return 0
  fi

  if [[ -d "${install_dir}/.git" ]]; then
    log "Using existing clone at ${install_dir}"
    git -C "${install_dir}" fetch --tags origin 2>/dev/null || true
    if [[ -n "${git_ref}" && "${git_ref}" != "HEAD" ]]; then
      git -C "${install_dir}" checkout "${git_ref}"
    fi
    return 0
  fi

  if [[ -d "${install_dir}" ]] && [[ -n "$(ls -A "${install_dir}" 2>/dev/null)" ]]; then
    die "Install directory ${install_dir} exists and is not a git clone"
  fi

  log "Cloning ${git_url} -> ${install_dir} (ref: ${git_ref:-default branch})"
  if [[ -n "${git_ref}" && "${git_ref}" != "HEAD" && "${git_ref}" != "main" ]]; then
    if ! git clone --depth 1 --branch "${git_ref}" "${git_url}" "${install_dir}" 2>/dev/null; then
      log "Branch clone failed; trying full clone for ref ${git_ref}"
      git clone "${git_url}" "${install_dir}"
      git -C "${install_dir}" checkout "${git_ref}"
    fi
  else
    git clone --depth 1 "${git_url}" "${install_dir}"
  fi
}

install_git_pull() {
  local install_dir="$1"
  if [[ ! -d "${install_dir}/.git" ]]; then
    die "Not a git repository: ${install_dir}"
  fi
  log "Pulling latest in ${install_dir}"
  git -C "${install_dir}" pull --ff-only
}
