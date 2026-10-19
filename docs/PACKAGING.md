# Packaging and releases

For maintainers: how the release files are built, how to build each one locally, and how a release is cut. Users
install StreamZH with [INSTALL.md](INSTALL.md).

## Release files

| File | Platform | Built by |
|---|---|---|
| `StreamZH-<version>-win-x64-setup.exe` | Windows 10 1809+ / Server 2019+, x64 | `packaging/windows/StreamZH.iss` (Inno Setup 6) |
| `StreamZH-<version>-win-x64.zip` | Windows, portable | `packaging/windows/build.ps1` |
| `streamzh_<version>_amd64.deb`, `_arm64.deb` | Debian, Ubuntu | `packaging/linux/build.sh` (nfpm) |
| `streamzh-<version>.x86_64.rpm`, `.aarch64.rpm` | Fedora, RHEL and derivatives | `packaging/linux/build.sh` (nfpm) |
| `StreamZH-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` | any Linux with systemd (`install-linux.sh`) | `packaging/linux/build.sh` |
| `StreamZH-<version>-osx-arm64.zip`, `-osx-x64.zip` | macOS (not notarized) | release workflow |
| `SHA256SUMS` | checksums of all of the above | release workflow |
| `ghcr.io/streamzh/streamzh:<version>` | Docker, linux/amd64 and linux/arm64 | `docker/Dockerfile` |

Every release file and the image also get a build provenance attestation: proof that the release workflow of
`streamzh/streamzh` built them from the tagged commit (`SHA256SUMS` alone only proves a download is complete). Check
one with the GitHub CLI:

```sh
gh attestation verify StreamZH-<version>-win-x64-setup.exe --repo streamzh/streamzh
gh attestation verify oci://ghcr.io/streamzh/streamzh:<version> --repo streamzh/streamzh
```

`<version>` is `<Version>` in `Directory.Build.props` (for example `1.0.0` or `1.0.0-rc.1`). The packages contain
`deb` and `rpm` metadata versions like `1.0.0~rc.1-1`, so a release sorts after its release candidates.

Every program is published **self-contained as a single file** (`StreamZH`, `StreamZH.exe`, and on Windows
`StreamZH.Tray.exe`), so no .NET runtime is needed. The single file unpacks its native libraries into `$HOME/.net`
(for the Linux service: `/var/lib/streamzh/.net`; systemd sets `HOME` for `User=`). The Docker image is the exception:
framework-dependent on the official ASP.NET image.

The update check in StreamZH (Settings → General → Updates) offers the file that matches the platform, the processor
and how StreamZH was installed. The setup and the Linux packages write a marker `package-type` next to the program
(`windows-installer`, `deb`, `rpm`); without it StreamZH offers the `.zip`/`.tar.gz`. Keep the file names above when
changing the workflow: `src/StreamZH.Host/General/ReleaseAssets.cs` matches on their endings.

## The repository: streamzh/streamzh

StreamZH lives in the GitHub organization **StreamZH** (a CloudZH product; the GitHub name "cloudzh" belongs to an unrelated account and must never be used): <https://github.com/streamzh/streamzh>, image
`ghcr.io/streamzh/streamzh`. The organization must keep both the repository and its GHCR namespace: every StreamZH
installation's update check trusts the releases of this repository (and only those: release and download links must
start with `https://github.com/streamzh/streamzh/releases/`), and the compose example and the docs run this image.

The repository is written in:

- `src/StreamZH.Core/AppInfo.cs` (`Repository`: the update check and documentation links; **the** reference)
- `packaging/windows/StreamZH.iss` (`AppUrl`: publisher, support and update links in Windows' app list)
- `packaging/linux/nfpm.yaml` (`maintainer`, `homepage`, `Bugs`)
- `docker/Dockerfile` (OCI labels; the release workflow overrides `source` with the real repository)
- `docker/docker-compose.example.yml` (`image: ghcr.io/streamzh/streamzh:latest` and the download link)
- the documentation (`README.md`, `SECURITY.md`, `docs/*.md`)

`packaging/check-repository.sh` checks that they agree with `AppInfo.Repository` (CI runs it on every push). The
release workflow runs it first with `--repository $GITHUB_REPOSITORY` and stops when the workflow runs in another
repository (a fork, a renamed organization): a release there would publish files and an image that no installation's
update check and no compose file points to. To move the repository, change `AppInfo.Repository` and the places above
in one commit; the image name follows `AppInfo.Repository`.

## Build locally

All commands from the repository root. First the web UI (it goes into `src/StreamZH.Host/wwwroot`):

```sh
cd frontend && npm ci && npm run build && cd ..
```

### Linux: .tar.gz, .deb, .rpm

```sh
dotnet publish src/StreamZH.Host -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out/linux-x64

# nfpm: download the release binary from https://github.com/goreleaser/nfpm/releases (the workflow pins the version)
packaging/linux/build.sh --rid linux-x64 --input out/linux-x64 --output dist --nfpm /path/to/nfpm
```

Without nfpm, `--no-packages` builds only the `.tar.gz`. For arm64 use `linux-arm64` in both commands (cross
publishing works from x64).

Check a package:

```sh
dpkg-deb -I dist/streamzh_<version>_amd64.deb      # control fields, maintainer scripts
dpkg-deb -c dist/streamzh_<version>_amd64.deb      # files
```

The maintainer scripts are in `packaging/linux/scripts/`. They handle both the deb (`configure`, `remove`, `purge`,
`upgrade`) and the rpm (`1`, `2`, `0`) arguments:

| Step | What happens |
|---|---|
| install | user and group `streamzh` (system, no shell, home `/var/lib/streamzh`), data folder `0750`, `systemctl enable --now streamzh` |
| upgrade | files replaced, `systemctl try-restart streamzh` (only restarts when it was running) |
| remove | `systemctl disable --now streamzh`; data and user stay |
| purge (deb only) | `/var/lib/streamzh` and the user are deleted |

Without systemd (containers, chroots) the scripts skip the service steps and print how to start StreamZH by hand.
If `install-linux.sh` installed StreamZH before, the package moves its unit
(`/etc/systemd/system/streamzh.service`) aside; `install-linux.sh` itself refuses to touch a package installation.

A full test in a throwaway root (no systemd there, so StreamZH is started by hand):

```sh
sudo unshare -m bash -c '
  mkdir -p /tmp/t/upper /tmp/t/work /tmp/t/root
  mount -t overlay overlay -o lowerdir=/,upperdir=/tmp/t/upper,workdir=/tmp/t/work /tmp/t/root
  mount -t proc proc /tmp/t/root/proc && mount --bind /dev /tmp/t/root/dev
  cp dist/streamzh_*_amd64.deb /tmp/t/root/tmp/
  chroot /tmp/t/root sh -c "dpkg -i /tmp/streamzh_*_amd64.deb && dpkg -r streamzh && dpkg -P streamzh"'
```

### Windows: installer and zip

On Windows, with the .NET 10 SDK and Inno Setup 6.7.0 (the release workflow installs exactly this version from
<https://github.com/jrsoftware/issrc/releases> and checks its SHA-256; `INNO_VERSION` and `INNO_SHA256` in
`release.yml`):

```powershell
$publish = '-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
  '-o', 'out\win-x64'
dotnet publish src\StreamZH.Host @publish
dotnet publish src\StreamZH.Tray @publish
.\packaging\windows\build.ps1 -SourceDir out\win-x64 -OutputDir dist
```

`build.ps1 -NoInstaller` builds only the zip; ISCC alone:
`ISCC.exe /DSourceDir=out\win-x64 /DOutputDir=dist packaging\windows\StreamZH.iss` (the version is read from
`Directory.Build.props`, or `/DAppVersion=1.2.3`).

What the setup does (details in the header of `StreamZH.iss`):

- Installs per machine to `C:\Program Files\StreamZH`. Only administrators may change that folder, which is what
  StreamZH's program-folder check expects (status notice "Program folder not protected" otherwise). The setup runs
  scripts from the program folder as administrator, so it refuses a folder outside Program Files unless it is started
  with `/ALLOWCUSTOMDIR`; it then creates that folder administrators-only before it copies anything (owner
  Administrators, nothing inherited; a link or a folder that already holds files is refused).
- The data folder: the scripts refuse a junction or symbolic link, make Administrators the owner and write the exact
  access list (SYSTEM, Administrators, the service account, the installing user). A `C:\ProgramData\StreamZH` that
  another account created and filled before the first installation is refused with instructions; an empty one is
  created anew. StreamZH shows the notice "Data folder not private" when another person's account owns it or a secret folder (keys, Backups, restore, tools) is a link.
- Data stays in `C:\ProgramData\StreamZH`, through upgrades and uninstalling.
- Wizard: **Windows service** (default) or **tray icon only**; firewall rule for private networks (TCP 9797, off by
  default); allow StreamZH in Controlled folder access (off by default); up to three folders the service account may
  change (only for the service).
- Runs the bundled scripts so there is one implementation: `tools\Install-Service.ps1 -NonInteractive` (service) or
  `tools\Install-Tray.ps1 -NonInteractive -NoStart` (tray only; the setup then starts the tray as the signed-in user
  with `--start`, which starts StreamZH). The script output goes into the setup log
  (`%TEMP%\Setup Log <date> #<n>.txt`). The service folders reach the script in a file (`-FoldersFile`), never as
  PowerShell code.
- Tray only is refused when the setup runs as SYSTEM (Intune, SCCM, RMM tools): StreamZH would run as SYSTEM. Such
  deployments use `/TASKS="service"`; an existing tray-only installation is upgraded by replacing the files only. The
  tray icon itself never runs as SYSTEM.
- Upgrades in place: stops the service, the tray icons and a StreamZH in a console window, replaces the files
  (the old `wwwroot` is removed first), runs the script again with the remembered choices.
- An earlier manual installation (service running `C:\Tools\StreamZH\StreamZH.exe`): the script points the service at
  the new program folder; the finish page says the old folder can be deleted. The data folder is the same.
- Opens <http://localhost:9797> at the end (after StreamZH answers on `/health`).
- Uninstall: `tools\Uninstall-Service.ps1` (service, firewall rule, event log source, tray autostart), stops StreamZH,
  removes the program. The data is deleted only when "Also delete data" is ticked; Inno Setup's `DelTree` deletes
  junctions and symbolic links inside the data folder without following them.

Silent use:

```powershell
StreamZH-1.0.0-win-x64-setup.exe /VERYSILENT /TASKS="service,firewall" /FOLDERS="D:\Media;D:\Downloads"
StreamZH-1.0.0-win-x64-setup.exe /VERYSILENT /TASKS="trayonly"
StreamZH-1.0.0-win-x64-setup.exe /VERYSILENT /ALLOWCUSTOMDIR /DIR="D:\Apps\StreamZH"
"C:\Program Files\StreamZH\unins000.exe" /VERYSILENT /DELETEDATA=1
```

The portable zip contains the same program and `tools\*.ps1`: unpack it anywhere, then `tools\Install-Service.ps1`
or `tools\Install-Tray.ps1` (as administrator) use the unpacked folder.

The `.iss` compiles on Linux too, with Inno Setup under Wine (32-bit Wine needed); the PowerShell steps of course only
run on Windows. The decisions of the scripts that need no Windows (quoting, what happens to a prepared data folder)
are tested with `pwsh -NoProfile -File tests/scripts/ServiceHelpers.Tests.ps1` (CI runs it).

### macOS: zip

Build on a Mac: the .NET SDK signs the program ad hoc there, which Apple silicon requires.

```sh
dotnet publish src/StreamZH.Host -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out/osx-arm64
mkdir -p stage/StreamZH && cp -R out/osx-arm64/. stage/StreamZH/ && cp LICENSE NOTICE.md stage/StreamZH/
(cd stage && zip -r -y -X ../dist/StreamZH-<version>-osx-arm64.zip StreamZH)
```

The zip is not notarized; users remove the quarantine flag once (`xattr -dr com.apple.quarantine StreamZH`).

### Docker image

```sh
docker build -f docker/Dockerfile --build-arg VERSION=<version> -t streamzh:dev .
docker buildx build -f docker/Dockerfile --platform linux/amd64,linux/arm64 -t streamzh:dev .   # both platforms
```

The web UI and the .NET build run on the build machine's platform; only the final stage (ASP.NET runtime, `par2`,
`7zip`, `ffmpeg`) is per platform. Ports: 9797 (web UI) and 51414 TCP+UDP (built-in torrent engine). Labels follow
the OCI keys (`source`, `licenses`, `version`, ...).

## Cutting a release

1. Set `<Version>` in `Directory.Build.props` (for example `1.0.0-rc.1`).
2. Add the `## [1.0.0-rc.1] - <date>` section to `CHANGELOG.md`; it becomes the release notes.
3. Commit, then tag and push: `git tag v1.0.0-rc.1 && git push origin v1.0.0-rc.1`.
4. `.github/workflows/release.yml` runs:
   - checks that it runs in `streamzh/streamzh` and that the docs agree (`packaging/check-repository.sh`), and that the
     tag equals `v<Version>` (fails otherwise),
   - builds the web UI and runs all tests,
   - builds every package (Linux on Ubuntu with the pinned nfpm, Windows on `windows-latest` with the pinned Inno
     Setup, macOS on `macos-latest`),
   - builds the Docker image and runs the smoke test (`/health`, `/api/v1/auth/state`, `/`),
   - in the environment `release`: pushes `ghcr.io/streamzh/streamzh` for amd64 and arm64 with the tags `<version>`,
     `<major>.<minor>` and `latest` (`<major>.<minor>` and `latest` only for versions without a suffix) and attests
     the image,
   - in the environment `release`: writes `SHA256SUMS`, attests every file and creates a **draft** GitHub release
     (pre-release when the version has a suffix) with all files and the notes.
5. Approve the two `release` jobs when GitHub asks (see below), check the draft (files, notes), then publish it. Only
   published, non-pre-release versions count as "latest" for the update check in StreamZH.
6. The first time: make the GHCR package public (GitHub → Packages → streamzh → Package settings).

"Run workflow" on the Release workflow builds and tests everything as a dry run: packages as workflow artifacts, the
image is built and smoke-tested but not pushed, no release. Pushes to `main` and pull requests run only `ci.yml`
(build, tests, shellcheck, the repository check, the PowerShell parser and the script tests).

### Repository settings the owner must make (once)

The workflows can only ask for these; GitHub enforces them only when they are set in the repository:

- **Environment `release`** (Settings → Environments; the first release run creates it empty): add **required
  reviewers** (the maintainers) and limit it to tags `v*` (Deployment branches and tags → Selected → tag pattern
  `v*`). Then nobody with only write access can publish an image or a release by pushing a tag.
- **Tag ruleset** for `v*` (Settings → Rules → Rulesets → New tag ruleset, target `v*`): restrict creation, update and
  deletion to the maintainers, so a release tag cannot be created or moved by others.
- **Dependabot** (`.github/dependabot.yml`): weekly pull requests for the pinned actions (the workflows pin every
  action to a commit, with the version as a comment), monthly for NuGet and npm. Review the commit an update moves to.
