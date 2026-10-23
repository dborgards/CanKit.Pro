#!/usr/bin/env python3
"""Assert that every packed .nupkg has an SBOM that says what the BSI guideline requires.

    verify-sboms.py <SBOM directory> <directory with .nupkg files> <version>

Checked against BSI TR-03183-2 (CycloneDX mapping), per SBOM:
  * CycloneDX 1.6 or higher, a serial number, a timestamp, an SBOM creator with a URL or e-mail;
  * the described component is the package: name, version, purl, creator, licence, and a SHA-512
    that is the hash of the .nupkg actually sitting in the artifact directory;
  * every component has name, version, creator, licence, SHA-512, purl, and the filename /
    executable / archive / structured properties with legal values;
  * the dependency graph has a node for every component, no dangling reference, and a statement
    that it is complete;
  * nothing is left at the 0.0.0 the restore stamps on sibling projects;
  * cross-check against the package itself: every dependency the .nuspec declares is in the SBOM,
    at a version no lower than the floor the .nuspec declares for it.

What this does NOT do is validate the document against the CycloneDX JSON schema -- that needs
the schema file and a validator, neither of which belongs in a repository whose scripts are
standard-library only. docs/supply-chain.md says how to run it.
"""

import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

EXPECTED_PACKAGE_COUNT = 9
PROPERTY_VALUES = {
    'bsi:component:executable': {'executable', 'non-executable'},
    'bsi:component:archive': {'archive', 'no archive'},
    'bsi:component:structured': {'structured', 'unstructured'},
}


def sha512(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b''):
            digest.update(chunk)
    return digest.hexdigest()


def local_name(node) -> str:
    return node.tag.rpartition('}')[2]


def declared_dependencies(package: Path) -> dict:
    """Dependency id (lower case) -> the version floors the .nuspec declares for it, one per
    target-framework group that names it, each as (version, exclusive). A dependency with no
    lower bound maps to an empty list."""
    with zipfile.ZipFile(package) as archive:
        name = next(n for n in archive.namelist() if n.endswith('.nuspec') and '/' not in n)
        nuspec = ElementTree.fromstring(archive.read(name))
    declared = {}
    for node in nuspec.iter():
        if local_name(node) == 'dependency' and node.get('id'):
            floors = declared.setdefault(node.get('id').lower(), [])
            floor = version_floor(node.get('version', ''))
            if floor[0]:
                floors.append(floor)
    return declared


def version_floor(range_text: str) -> tuple:
    """The lower bound of a NuGet version range as (version, exclusive): '1.2.3', '[1.2.3, )' and
    '[1.2.3]' give ('1.2.3', False), '(1.2.3, 2.0)' gives ('1.2.3', True), and '(, 2.0)' and ''
    have no lower bound: ('', False)."""
    text = range_text.strip()
    if text[:1] in ('[', '('):
        floor = text[1:].split(',')[0].strip(' ]),')
        return floor, bool(floor) and text[0] == '('
    return text, False


def version_key(version: str) -> tuple:
    """A sort key for SemVer 2 / NuGet versions: numeric parts first, a pre-release below the
    release it precedes, build metadata ignored."""
    core, _, rest = version.partition('+')[0].partition('-')
    numbers = tuple(int(part) if part.isdigit() else 0 for part in core.split('.'))
    numbers += (0,) * (4 - len(numbers))
    if not rest:
        return numbers, (1,)
    return numbers, (0,) + tuple((0, int(i), '') if i.isdigit() else (1, 0, i) for i in rest.split('.'))


def below_floor(declared: dict, components: list) -> list:
    """One message per declared dependency whose SBOM component is older than the .nuspec's floor.

    The SBOM is the restored closure, which can legitimately sit above a floor (NuGet takes the
    lowest version that satisfies every constraint, so a transitive constraint can lift it) and
    never legitimately below one. A package with several target-framework groups may declare a
    different floor in each, and the SBOM is their union, so the highest version listed has to
    reach the highest floor declared -- equality is not guaranteed."""
    listed = {}
    for component in components:
        listed.setdefault(str(component.get('name', '')).lower(), []).append(str(component.get('version', '')))
    problems = []
    for dependency, floors in sorted(declared.items()):
        if not floors or dependency not in listed:
            continue
        # The strongest floor: the highest version, and at the same version an exclusive bound.
        floor, exclusive = max(floors, key=lambda f: (version_key(f[0]), f[1]))
        highest = max(listed[dependency], key=version_key)
        if version_key(highest) < version_key(floor) or (exclusive and version_key(highest) == version_key(floor)):
            problems.append(f'the .nuspec requires {dependency} {">" if exclusive else ">="} {floor}, '
                            f'the SBOM lists {highest}')
    return problems


def creator_problem(owner: str, manufacturer) -> str:
    if not isinstance(manufacturer, dict):
        return f'{owner}: no manufacturer (the creator, TR-03183-2 §5.2)'
    has_url = any(manufacturer.get('url', []))
    has_mail = any(c.get('email') for c in manufacturer.get('contact', []))
    if not (has_url or has_mail):
        return f'{owner}: manufacturer has neither a URL nor a contact e-mail'
    return ''


def licence_problem(owner: str, component: dict) -> str:
    licences = component.get('licenses', [])
    for entry in licences:
        inner = entry.get('license', {})
        # An SPDX id or expression, not a free-text name: TR-03183-2 asks for SPDX identifiers,
        # and "Unknown - See URL" -- what the tool writes for a licenseUrl-only .nuspec -- is a
        # name.
        if inner.get('id') or entry.get('expression'):
            return ''
    return f'{owner}: no SPDX licence id or expression (TR-03183-2 §5.2)'


def component_problems(owner: str, component: dict) -> list:
    problems = []
    for field in ('name', 'version', 'purl', 'bom-ref'):
        if not component.get(field):
            problems.append(f'{owner}: no {field}')

    for problem in (creator_problem(owner, component.get('manufacturer')),
                    licence_problem(owner, component)):
        if problem:
            problems.append(problem)

    hashes = {h.get('alg'): h.get('content', '') for h in component.get('hashes', [])}
    if not re.fullmatch(r'[0-9a-fA-F]{128}', hashes.get('SHA-512', '')):
        problems.append(f'{owner}: no SHA-512 hash (TR-03183-2 requires it, not SHA-256)')

    properties = {p['name']: p['value'] for p in component.get('properties', [])}
    if not properties.get('bsi:component:filename'):
        problems.append(f'{owner}: no bsi:component:filename')
    for name, allowed in PROPERTY_VALUES.items():
        if properties.get(name) not in allowed:
            problems.append(f'{owner}: {name} is {properties.get(name)!r}, expected one of {sorted(allowed)}')

    if component.get('version') == '0.0.0':
        problems.append(f'{owner}: version 0.0.0 -- a sibling project the restore did not version')
    return problems


def check(document: dict, package_id: str, package: Path, version: str) -> list:
    problems = []

    if document.get('bomFormat') != 'CycloneDX':
        problems.append('bomFormat is not CycloneDX')
    spec = tuple(int(part) for part in str(document.get('specVersion', '0.0')).split('.')[:2])
    if spec < (1, 6):
        problems.append(f'specVersion {document.get("specVersion")} is below 1.6')
    if not re.fullmatch(r'urn:uuid:[0-9a-fA-F-]{36}', document.get('serialNumber', '')):
        problems.append('no urn:uuid serialNumber')

    metadata = document.get('metadata', {})
    if not re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)',
                        metadata.get('timestamp', '')):
        problems.append('metadata.timestamp missing or not RFC 3339')
    problem = creator_problem('SBOM creator (metadata.manufacturer)', metadata.get('manufacturer'))
    if problem:
        problems.append(problem)

    root = metadata.get('component', {})
    problems += component_problems(f'metadata.component {package_id}', root)
    if root.get('name') != package_id:
        problems.append(f'metadata.component is {root.get("name")}, expected {package_id}')
    if root.get('version') != version:
        problems.append(f'metadata.component version is {root.get("version")}, expected {version}')
    if root.get('purl') != f'pkg:nuget/{package_id}@{version}':
        problems.append(f'metadata.component purl is {root.get("purl")}')
    if {h.get('alg'): h.get('content', '').lower() for h in root.get('hashes', [])}.get('SHA-512') != sha512(package):
        problems.append('metadata.component SHA-512 is not the hash of the .nupkg in the artifact directory')

    components = document.get('components', [])
    refs = [root.get('bom-ref')] + [c.get('bom-ref') for c in components]
    if len(set(refs)) != len(refs):
        problems.append('bom-ref values are not unique')

    own = {}
    for component in components:
        problems += component_problems(f'{component.get("name")}@{component.get("version")}', component)
        if str(component.get('name', '')).startswith('CanKit.Pro.'):
            own[component['name']] = component

    # What the package declares must be what the SBOM lists. The .nuspec is the ground truth for
    # "what does installing this pull in"; an SBOM missing one of its entries is wrong, however
    # well-formed.
    declared = declared_dependencies(package)
    listed = {str(c.get('name', '')).lower() for c in components}
    for dependency in sorted(set(declared) - listed):
        problems.append(f'the .nuspec depends on {dependency}, the SBOM does not list it')
    problems += below_floor(declared, components)

    graph = {entry.get('ref'): entry for entry in document.get('dependencies', [])}
    for ref in refs:
        if ref not in graph:
            problems.append(f'dependencies: no node for {ref}')
    for entry in graph.values():
        for target in entry.get('dependsOn', []):
            if target not in refs:
                problems.append(f'dependencies: {entry["ref"]} depends on unknown {target}')

    complete = any(c.get('aggregate') == 'complete' and root.get('bom-ref') in c.get('dependencies', [])
                   for c in document.get('compositions', []))
    if not complete:
        problems.append('no composition declaring the dependency list complete (TR-03183-2 §5.2.2)')

    return problems


def main(sbom_directory: str, package_directory: str, version: str) -> int:
    suffix = f'.{version}.nupkg'
    packages = {p.name[:-len(suffix)]: p for p in sorted(Path(package_directory).glob(f'*{suffix}'))}
    if not packages:
        print(f'no *{suffix} files in {package_directory}', file=sys.stderr)
        return 1

    failed = False
    for package_id, package in packages.items():
        path = Path(sbom_directory) / f'{package_id}.cdx.json'
        if not path.exists():
            failed = True
            print(f'FAIL  {path.name}\n        missing')
            continue
        problems = check(json.loads(path.read_text(encoding='utf-8')), package_id, package, version)
        if problems:
            failed = True
            print(f'FAIL  {path.name}')
            for problem in problems:
                print(f'        {problem}')
        else:
            print(f'ok    {path.name}')

    extra = sorted({p.name for p in Path(sbom_directory).glob('*.cdx.json')}
                   - {f'{package_id}.cdx.json' for package_id in packages})
    if extra:
        failed = True
        print(f'\nFAIL  SBOMs without a package: {", ".join(extra)}')

    if len(packages) != EXPECTED_PACKAGE_COUNT:
        failed = True
        print(f'\nFAIL  {len(packages)} packages, expected {EXPECTED_PACKAGE_COUNT} '
              f'(same constant as eng/verify-packages.py -- change both, and say so in the commit).')

    print(f'\n{len(packages)} SBOM(s) checked')
    return 1 if failed else 0


if __name__ == '__main__':
    if len(sys.argv) != 4:
        raise SystemExit(
            'usage: verify-sboms.py <SBOM directory> <directory with .nupkg files> <version>')
    sys.exit(main(*sys.argv[1:]))
