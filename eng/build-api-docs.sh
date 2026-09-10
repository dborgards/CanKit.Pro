#!/usr/bin/env bash
#
# Generates docs/api/ — the website's API reference — from the XML documentation comments of the
# nine shipping packages, using DefaultDocumentation (.config/dotnet-tools.json). Run it before
# mkdocs; .github/workflows/docs.yml does exactly that.
#
#   eng/build-api-docs.sh              # build the solution, then generate
#   eng/build-api-docs.sh --no-build   # reuse an existing Release build
#
# The output is not checked in (see .gitignore): it is derived from src/, and a copy in git could
# only ever be out of date. eng/DefaultDocumentation.json holds the generator settings; every path
# is passed here as an absolute path, because DefaultDocumentation resolves relative paths in that
# file against the file's own directory rather than the working directory.
#
# Two generation passes are needed. The first only collects each package's links file — the map
# from documentation id to page — and the second feeds every package the other eight maps, so that
# a cross-package `<see cref>` (ISO-TP naming ICanBusService, say) becomes a real link instead of
# a dead reference. eng/api-docs-postprocess.py renames the pages in between and writes the
# overview and the SUMMARY.md that mkdocs-literate-nav turns into the API tab.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

packages=(
  CanKit.Pro.RawCan
  CanKit.Pro.Actor
  CanKit.Pro.Addressing
  CanKit.Pro.Reliability
  CanKit.Pro.IsoTp
  CanKit.Pro.J1939Tp
  CanKit.Pro.Uds
  CanKit.Pro.CANopen
  CanKit.Pro.J1939
)

# The documentation is generated from the modern target; netstandard2.0 offers the same API.
tfm="net10.0"
config="$repo_root/eng/DefaultDocumentation.json"
api_dir="$repo_root/docs/api"
python="${PYTHON:-python3}"

no_build=0
for arg in "$@"; do
  case "$arg" in
    --no-build) no_build=1 ;;
    *) echo "usage: $0 [--no-build]" >&2; exit 2 ;;
  esac
done

echo "==> restoring dotnet tools"
dotnet tool restore

if [ "$no_build" -eq 0 ]; then
  echo "==> building CanKit.Pro.sln (Release)"
  dotnet build CanKit.Pro.sln -c Release --nologo -v minimal
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/links"

generate() {
  # generate <package> <output-dir> [extra args...]
  local package="$1" output="$2"
  shift 2
  local assembly="$repo_root/src/$package/bin/Release/$tfm/$package.dll"
  if [ ! -f "$assembly" ]; then
    echo "$assembly is missing — run without --no-build." >&2
    exit 1
  fi
  dotnet defaultdocumentation \
    --ConfigurationFilePath "$config" \
    --AssemblyFilePath "$assembly" \
    --ProjectDirectoryPath "$repo_root/src/$package" \
    --OutputDirectoryPath "$output" \
    --LogLevel Warning \
    "$@"
}

echo "==> pass 1/2: collecting cross-package links"
for package in "${packages[@]}"; do
  generate "$package" "$work/pass1/$package" \
    --LinksOutputFilePath "$work/links/$package.links" \
    --LinksBaseUrl "../$package/"
  "$python" eng/api-docs-postprocess.py links "$work/links/$package.links" "$package"
done

echo "==> pass 2/2: writing docs/api"
rm -rf "$api_dir"
mkdir -p "$api_dir"
for package in "${packages[@]}"; do
  extern=()
  for other in "${packages[@]}"; do
    [ "$other" = "$package" ] || extern+=("$work/links/$other.links")
  done
  generate "$package" "$api_dir/$package" --ExternLinksFilePaths "${extern[@]}"
done

"$python" eng/api-docs-postprocess.py pages "$api_dir" "${packages[@]}"
