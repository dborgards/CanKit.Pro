#!/usr/bin/env python3
"""Generate one CycloneDX SBOM per packed .nupkg.

    generate-sboms.py <directory with .nupkg files> <version> <output directory>

A package is the unit a consumer installs, so it is the unit that gets a bill of materials: one
`<PackageId>.cdx.json` per `<PackageId>.<version>.nupkg`, listing the package, everything it
resolves to at install time, and the dependency graph between them. The packages are produced by
`dotnet pack` first; this script runs *after* it and never builds anything itself.

The raw SBOM comes from the CycloneDX .NET tool (pinned in .config/dotnet-tools.json), which reads
the resolved dependency closure from each project's project.assets.json. That closure is the union
over every target framework of the project -- a deliberate superset: the SBOM does not know which
framework a consumer will pick, and a bill of materials that omits something a consumer might
install is worse than one that lists a polyfill they do not.

What the tool does not know, and this script adds, is what BSI TR-03183-2 (the SBOM guideline the
CRA's implementing practice leans on) asks for and a build-time scan cannot see:

  * the SBOM creator and each component's creator (`manufacturer`);
  * the SHA-512 of the *deployable* component -- for the CanKit.Pro packages that is the .nupkg
    in the artifact directory, hashed here, so the SBOM describes the bytes that were published;
  * the filename, and the executable / archive / structured properties;
  * the completeness of the dependency list (`compositions`).

It also repairs one thing: sibling packages (CanKit.Pro.CANopen -> CanKit.Pro.Actor) are project
references, and the restore the tool runs does not carry the release version, so the tool reports
them as 0.0.0. They are rewritten to the version that was packed.

Verification is a separate step on purpose: eng/verify-sboms.py, run over this script's output.
"""

import hashlib
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
REPOSITORY_URL = 'https://github.com/dborgards/CanKit.Pro'

# TR-03183-2 accepts "a contact e-mail address, or a URL if no e-mail is available". A URL: an
# address in a public, signed release artifact is something the maintainer should add on purpose.
CREATOR = {'name': 'Dietmar Borgards', 'url': [REPOSITORY_URL]}

# "Spec 1.6 or higher" is the guideline's floor; 1.6 is what the tooling around it (validators,
# Dependency-Track) handles most widely. The tool's own default, 1.7, is newer than that.
SPEC_VERSION = '1.6'

PACKAGE_PREFIX = 'CanKit.Pro.'

# Older Microsoft packages declare their licence only as a <licenseUrl>, which the tool cannot map
# to SPDX and writes as "Unknown - See URL". The guideline asks for SPDX, so the URLs whose
# target is known are mapped here -- by exact URL, never by guessing from the package name: the
# same vendor's Microsoft.NETCore.Platforms 1.1.0 points elsewhere, at the old .NET Library
# licence, and must not be relabelled. A URL not in this table stays unmapped and fails
# verify-sboms.py, which is the point.
LICENSE_URLS = {
    # The corefx repository's LICENSE.TXT: the MIT licence.
    'https://github.com/dotnet/corefx/blob/master/LICENSE.TXT': 'MIT',
}


def sha(algorithm: str, path: Path) -> str:
    digest = hashlib.new(algorithm)
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b''):
            digest.update(chunk)
    return digest.hexdigest()


def set_properties(component: dict, filename: str) -> None:
    """The four component properties TR-03183-2 §5.2 maps onto CycloneDX.

    A .nupkg is a zip: an archive, and a structured one (it carries a .nuspec). It is not itself
    executable -- the guideline reserves that for code a machine runs, and the DLLs inside are
    not listed as separate components, which is the granularity this SBOM stops at.
    """
    properties = [p for p in component.get('properties', []) if not p['name'].startswith('bsi:')]
    properties += [
        {'name': 'bsi:component:filename', 'value': filename},
        {'name': 'bsi:component:executable', 'value': 'non-executable'},
        {'name': 'bsi:component:archive', 'value': 'archive'},
        {'name': 'bsi:component:structured', 'value': 'structured'},
    ]
    component['properties'] = properties


def first_reference(component: dict, *kinds: str) -> str:
    for kind in kinds:
        for reference in component.get('externalReferences', []):
            if reference.get('type') == kind and reference.get('url'):
                return reference['url']
    return ''


def map_license_urls(component: dict) -> None:
    for entry in component.get('licenses', []):
        license_ = entry.get('license', {})
        spdx = LICENSE_URLS.get(license_.get('url', ''))
        if spdx and not license_.get('id'):
            license_.pop('name', None)
            license_['id'] = spdx


def enrich_third_party(component: dict) -> None:
    name, version = component['name'], component['version']
    map_license_urls(component)

    # The creator, as the package declares it. The URL is the project's own home page or
    # repository where the package names one, otherwise the package's page on nuget.org -- a real
    # page about this exact component, which is what the guideline wants where there is no e-mail.
    url = first_reference(component, 'website', 'vcs') or f'https://www.nuget.org/packages/{name}/{version}'
    authors = [a.get('name') for a in component.get('authors', []) if a.get('name')]
    component['manufacturer'] = {'name': authors[0] if authors else name, 'url': [url]}

    set_properties(component, f'{name}.{version}.nupkg')


def enrich_own(component: dict, package: Path) -> None:
    name, version = component['name'], component['version']

    component['manufacturer'] = dict(CREATOR)
    component['licenses'] = [{'license': {'id': 'MIT', 'acknowledgement': 'declared'}}]
    component['hashes'] = [
        {'alg': 'SHA-512', 'content': sha('sha512', package)},
        {'alg': 'SHA-256', 'content': sha('sha256', package)},
    ]

    references = [r for r in component.get('externalReferences', [])
                  if r.get('type') not in ('vcs', 'website', 'distribution', 'issue-tracker',
                                           'security-contact', 'advisories')]
    references += [
        {'type': 'website', 'url': REPOSITORY_URL},
        {'type': 'vcs', 'url': REPOSITORY_URL},
        {'type': 'issue-tracker', 'url': f'{REPOSITORY_URL}/issues'},
        # The URI of the deployable form (TR-03183-2 "additional" field).
        {'type': 'distribution', 'url': f'https://www.nuget.org/api/v2/package/{name}/{version}'},
        # SECURITY.md is the reporting route, and the advisories are where a fix is announced.
        {'type': 'security-contact', 'url': f'{REPOSITORY_URL}/security/policy'},
        {'type': 'advisories', 'url': f'{REPOSITORY_URL}/security/advisories'},
    ]
    component['externalReferences'] = references

    set_properties(component, package.name)


def rewrite_refs(document: dict, renames: dict) -> None:
    for entry in document.get('dependencies', []):
        entry['ref'] = renames.get(entry['ref'], entry['ref'])
        if 'dependsOn' in entry:
            entry['dependsOn'] = [renames.get(ref, ref) for ref in entry['dependsOn']]


def enrich(document: dict, package_id: str, version: str, packages: dict) -> None:
    root = document['metadata']['component']
    components = document.setdefault('components', [])

    # Sibling packages: 0.0.0 -> the packed version. Anything else is a real mismatch -- the
    # CanKit.Pro packages ship in lockstep, so a sibling at another version means the closure was
    # not resolved from this commit.
    renames = {}
    for component in components:
        if component['name'] in packages and component['version'] != version:
            if component['version'] != '0.0.0':
                raise SystemExit(f'{package_id}: sibling {component["name"]} resolved to '
                                 f'{component["version"]}, expected {version}')
            old = component['bom-ref']
            component['version'] = version
            component['purl'] = f'pkg:nuget/{component["name"]}@{version}'
            component['bom-ref'] = component['purl']
            renames[old] = component['bom-ref']
    rewrite_refs(document, renames)

    enrich_own(root, packages[package_id])
    for component in components:
        if component['name'] in packages:
            enrich_own(component, packages[component['name']])
        else:
            enrich_third_party(component)

    document['metadata']['manufacturer'] = dict(CREATOR)
    document['metadata']['lifecycles'] = [{'phase': 'build'}]

    # TR-03183-2 §5.2.2: say whether the dependency list is complete. It is the resolved NuGet
    # closure (project.assets.json), which is exactly what a consumer's restore will install.
    refs = [root['bom-ref']] + [c['bom-ref'] for c in components]
    document['compositions'] = [{'aggregate': 'complete', 'dependencies': refs}]

    # A component without dependencies is still an (empty) node in the graph; the tool emits
    # them, and the guard keeps that true should a future tool version stop doing so.
    known = {entry['ref'] for entry in document.get('dependencies', [])}
    for ref in refs:
        if ref not in known:
            document.setdefault('dependencies', []).append({'ref': ref})


def generate(package_id: str, version: str, packages: dict, output: Path) -> Path:
    project = REPOSITORY_ROOT / 'src' / package_id / f'{package_id}.csproj'
    if not project.exists():
        raise SystemExit(f'{package_id}: no project at {project.relative_to(REPOSITORY_ROOT)}')

    with tempfile.TemporaryDirectory() as scratch:
        command = [
            'dotnet', 'dotnet-CycloneDX', str(project),
            '--output', scratch, '--filename', f'{package_id}.cdx.json',
            '--output-format', 'Json', '--spec-version', SPEC_VERSION,
            '--set-type', 'Library', '--set-name', package_id, '--set-version', version,
            '--include-project-references', '--set-nuget-purl',
            # Build-time-only packages (NETStandard.Library, Microsoft.NETCore.Platforms) are not
            # part of what ships or what a consumer restores. Left in, they arrive scope
            # "excluded" with a non-SPDX licence ("Unknown - See URL": the latter is under the old
            # Microsoft .NET Library licence, so it cannot be relabelled MIT) and fail the
            # guideline's SPDX requirement for a component that is not even in the product. The
            # flag is deprecated in tool 6.x but still filters; the tool is pinned, and
            # verify-sboms.py fails if a licence is not SPDX, so a tool that stops filtering is
            # noticed here rather than shipped.
            '--exclude-dev',
        ]
        result = subprocess.run(command, cwd=REPOSITORY_ROOT, capture_output=True, text=True)
        if result.returncode != 0:
            sys.stderr.write(result.stdout + result.stderr)
            raise SystemExit(f'{package_id}: the CycloneDX tool failed ({result.returncode})')

        document = json.loads((Path(scratch) / f'{package_id}.cdx.json').read_text(encoding='utf-8'))

    enrich(document, package_id, version, packages)

    target = output / f'{package_id}.cdx.json'
    target.write_text(json.dumps(document, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    return target


def main(directory: str, version: str, output_directory: str) -> int:
    if not re.fullmatch(r'\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?', version):
        raise SystemExit(f'"{version}" is not a SemVer version')

    suffix = f'.{version}.nupkg'
    packages = {p.name[:-len(suffix)]: p for p in sorted(Path(directory).glob(f'*{suffix}'))}
    if not packages:
        raise SystemExit(f'no *{suffix} files in {directory}')

    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)

    # The tool is a repository-local one; restoring is idempotent and cheap when it is current.
    subprocess.run(['dotnet', 'tool', 'restore'], cwd=REPOSITORY_ROOT, check=True,
                   capture_output=True, text=True)

    for package_id in packages:
        target = generate(package_id, version, packages, output)
        print(f'wrote {target}')

    print(f'\n{len(packages)} SBOM(s) in {output}')
    return 0


if __name__ == '__main__':
    if len(sys.argv) != 4:
        raise SystemExit(
            'usage: generate-sboms.py <directory with .nupkg files> <version> <output directory>')
    sys.exit(main(*sys.argv[1:]))
