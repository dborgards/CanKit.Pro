# Supply chain: SBOMs and attestations

Every release publishes, next to the nine NuGet packages, **one CycloneDX SBOM per package** and two
signed attestations per package. This page says what is in them, what is deliberately not, and how to
check them.

## Is this required?

Not for this project, as far as the EU Cyber Resilience Act (Regulation (EU) 2024/2847) is concerned —
and this is a reading of the text, not legal advice. The CRA's obligations fall on *manufacturers* who
make a product available **in the course of a commercial activity**; a library released under the MIT
licence without payment or paid support is not that. Where it does apply — to whoever builds a product
*with* these packages — the vulnerability-handling duty in Annex I, Part II, point 1 asks the manufacturer
for an SBOM "in a commonly used and machine-readable format covering at the very least the top-level
dependencies", kept in the technical documentation. It does not have to be published. At the time of
writing the Commission has not adopted an implementing act that fixes the SBOM format (Article 13(24)).

So the SBOMs here are **voluntary, and for the benefit of the people who must have one**: a manufacturer
who ships a product built on CanKit.Pro can take the package's SBOM as the input for their own, instead of
reconstructing the dependency closure by hand.

Nothing here is a certification. In particular, no one has checked these SBOMs against a conformance
tester, and the CRA does not name a technical standard for them yet.

## What is published

| Artefact | Where | Made by |
| --- | --- | --- |
| `CanKit.Pro.<Name>.cdx.json` — CycloneDX 1.6 JSON, one per package | assets of the GitHub Release | `eng/generate-sboms.py` |
| SBOM attestation — the SBOM bound to the `.nupkg` it describes | GitHub attestation store | `actions/attest` |
| Build-provenance attestation — which workflow and commit built the `.nupkg` | GitHub attestation store | `actions/attest` |

The SBOM describes the **package**, and lists everything installing it resolves to — across both target
frameworks, so the list is a superset: it includes the `netstandard2.0` polyfills (`System.Memory` and
friends) a `net10.0` consumer never installs. An SBOM that omits something a consumer might get is worse
than one that lists a package they happen not to.

## How it meets BSI TR-03183-2

The CRA names no format, so the usable reference is the German BSI's technical guideline
[TR-03183-2](https://www.bsi.bund.de/dok/TR-03183) (CycloneDX 1.6 or newer, or SPDX 3.0.1 or newer). The
mapping below is the author's reading of version 2.x, and `eng/verify-sboms.py` enforces what it can of it
on every pull request and again before a release.

| TR-03183-2 field | In the SBOM |
| --- | --- |
| SBOM creator | `metadata.manufacturer` — a URL, not an e-mail (the guideline allows either) |
| Timestamp | `metadata.timestamp` |
| Component creator | `manufacturer` on the package and on every component; for third parties the author the package declares, with the project's own URL or its nuget.org page |
| Name, version | `name`, `version` |
| Filename | `bsi:component:filename` — the `.nupkg` name |
| Dependencies and their completeness | `dependencies` (a node for every component, leaves included) and a `compositions` entry with `aggregate: complete` |
| Distribution licence | `licenses`, as an SPDX id; `eng/verify-sboms.py` rejects a free-text name |
| Hash of the deployable | SHA-512 in `hashes`. For `CanKit.Pro.*` it is the hash of the `.nupkg` in the release, computed from that file |
| Executable / archive / structured | `bsi:component:executable` = `non-executable`, `bsi:component:archive` = `archive`, `bsi:component:structured` = `structured` |
| Source code URI, URI of the deployable | `externalReferences` of type `vcs` and `distribution` |
| Unique identifier | `purl` (`pkg:nuget/…`) |

Beyond the guideline: `externalReferences` of type `security-contact` and `advisories` point at the
[security policy](https://github.com/dborgards/CanKit.Pro/security/policy), and `lifecycles` says the data
comes from the build.

### Where the reading is a judgement

- **Granularity is the package.** The DLLs inside a `.nupkg` are not listed as separate components. The
  guideline asks that an archive's contents be listed; for a NuGet package the contents *are* the
  package, and its third-party dependencies are listed. Whether a stricter reading would want the
  assemblies too is not settled by anything this repository can check.
- **A `.nupkg` is marked `non-executable`.** It is a zip; the code is in the assemblies inside.
- **The creator is a URL.** Add a contact e-mail to `CREATOR` in `eng/generate-sboms.py` if you want one.
- **"Complete" means the resolved NuGet closure.** The .NET runtime a consumer runs on is theirs.
- **Build-time-only packages are left out** (`NETStandard.Library`, `Microsoft.NETCore.Platforms`): they
  are not part of what ships or what a consumer restores, and one of them is licensed under a
  non-SPDX Microsoft licence.
- **Licences the tool cannot map** are mapped by exact `licenseUrl` in `eng/generate-sboms.py`
  (`LICENSE_URLS`). A new dependency whose `.nuspec` gives only an unknown URL makes the SBOM step fail,
  on the pull request that adds it; the fix is to add the URL to that table *after reading what it says*.

An SBOM is a list of components, not a statement that any of them is free of vulnerabilities. Nothing here
replaces a vulnerability scan of it (see below) or a VEX statement; this repository publishes neither.

## Checking a release

Take any package and its SBOM from the release page, then:

```bash
# The package was built by this repository's release workflow, from the commit it names.
gh attestation verify CanKit.Pro.RawCan.<version>.nupkg --repo dborgards/CanKit.Pro

# The SBOM is the one attested for that package.
gh attestation verify CanKit.Pro.RawCan.<version>.nupkg --repo dborgards/CanKit.Pro \
  --predicate-type https://cyclonedx.org/bom

# The SHA-512 in the SBOM is the package's.
sha512sum CanKit.Pro.RawCan.<version>.nupkg
jq -r '.metadata.component.hashes[] | select(.alg=="SHA-512") | .content' CanKit.Pro.RawCan.cdx.json
```

To scan an SBOM for known vulnerabilities, import it into
[Dependency-Track](https://dependencytrack.org/) or feed it to
[Grype](https://github.com/anchore/grype) (`grype sbom:CanKit.Pro.RawCan.cdx.json`).

To validate against the schema (the repository's own check is structural and does not do this):

```bash
git clone --depth 1 https://github.com/CycloneDX/specification
pip install check-jsonschema
check-jsonschema --schemafile specification/schema/bom-1.6.schema.json CanKit.Pro.RawCan.cdx.json
```

## Maintaining it

```bash
dotnet pack CanKit.Pro.sln -c Release -p:Version=9.9.9 -o artifacts/nuget
python3 eng/generate-sboms.py artifacts/nuget 9.9.9 artifacts/sbom
python3 eng/verify-sboms.py   artifacts/sbom   artifacts/nuget 9.9.9
```

The SBOMs are generated in the release workflow's `verify` job — the one with no credentials — from the
packages it has just packed, and handed on as an artifact. `prepareCmd` checks them again against the
downloaded packages before the tag exists, and `.releaserc.json` attaches them to the GitHub Release.

The attestations are made by the `attest` job, **after** the release, with its own `id-token` and
`attestations` permissions and no other credential. After, because an attestation for a version that then
fails to publish would vouch for something nobody can install. If that job fails, *Re-run failed jobs*
repeats it alone against the retained artifacts; nothing is released twice.

`CycloneDX` is pinned in `.config/dotnet-tools.json`. Its version changes the SBOM's content, not the
packages', so Dependabot groups it with the test tooling and a bump publishes no release.
