# Release

How to cut a release of this plugin, from the version number to the manifest every server reads.

## Versions

A version is `JJ.JJ.F.B`.

| Part | Means | Raise it when |
| :--- | :--- | :--- |
| `JJ.JJ` | The Jellyfin version the release targets, such as `12.4` | The plugin moves to a new Jellyfin version |
| `F` | The feature release | The release adds or changes behavior a user will notice |
| `B` | The bug release within that feature | The release only fixes bugs |

Raising a part sets every part after it to `0`. `12.4.2.4` becomes `12.4.3.0` for a feature and `12.4.2.5` for a fix.

## Branch

A feature release is cut on `unstable`, then **Sync Branches** with `promote` moves `master` to it. A bug release is cut on `master`, and the sync carries it to `unstable`. See [branches.md](branches.md).

## Before the Release

1. **Base.** When the release needs a Base change, release Base first with its **Release** workflow, then raise `JPKribs.Jellyfin.Base` in `Jellyfin.Plugin.LiveChannels/Jellyfin.Plugin.LiveChannels.csproj` and delete any local copy Base replaces. Wait until NuGet serves the version, and never release against a local package. Raise Base on a branch, and merge it once every check below has passed on it.
2. **Stored state.** When the schedule cache or the export file changed shape, make sure an older file still loads, or rebuilds cleanly, and say so in `changed`.
3. **Build.**

    ```bash
    dotnet build
    ```

4. **Tests.** Run the suite with jellyfin-ffmpeg found, then inside the Jellyfin image.

    ```bash
    LIVECHANNELS_REQUIRE_FFMPEG=1 dotnet test
    ```

    ```bash
    .github/scripts/test-in-jellyfin.sh jellyfin/jellyfin:12.4
    ```

5. **Pages.** Install the zip from `./build.sh` on a test server and open each tab, per [testing.md](testing.md#config-pages).

    Every check must run on the commit that is released. Run them again after any later change.

6. **Docs.** Every page describes this release. Retake any screenshot whose subject changed, and all of them when the Base version changed. Update [Versioning](../docs/Versioning.md) when the Jellyfin target or the upgrade path changed, and [How It Works](../docs/HowItWorks.md) when the parts or their connections changed.

    ```bash
    mkdocs build --strict
    ```

7. **Review.** Run [review.md](review.md) over everything since the last release.

## Set the Release

Follow these steps exactly, in this order. First merge the release branch into `master`, once every check above has passed on it, and work on `master` from here.

```bash
git checkout master
git merge --ff-only <branch>
```

Then edit `build.yaml`.

1. Set `version` to the new version.
2. Replace the whole `changelog` with this release's entries. Each release lists only its own changes. See [documentation.md](documentation.md#release-notes) for how to write them.
3. Name the Base version in `developer` when it changed, such as "Uses JPKribs.Jellyfin.Base 12.4.2", with what it replaced locally.
4. Keep `description` and `overview` true for this release. `manifest.json` takes its own description, so update both together.

Commit it on its own.

```bash
git commit -am "Set the release to 12.4.3.0"
```

```bash
git push
```

## Publish

1. **Draft.** The **Create/Update Release Draft** workflow builds the zip and writes a draft release named `v<version>` with the changelog as its notes. The push above starts it, since it changes files outside the docs and `.github`. Start it from the Actions tab only when the push did not. Draft builds run one at a time, so a push and a manual start that overlap build one after the other.
2. **Check.** Wait until no draft build is queued or running, since each one replaces the zip. Then open the draft on GitHub. The notes must match `build.yaml`, and the zip and its `.md5` must be attached.
3. **Release.** Publish the draft. A draft build never replaces the assets of a published release.
4. **Manifest.** The **Publish Plugin** workflow waits for any draft build still running, hashes the attached zip, checks it against the `.md5`, adds the version to `manifest.json`, and commits "Update manifest for release v<version>". Check that the commit exists and that its checksum matches the release's `.md5`.
5. **Pull.**

    ```bash
    git pull
    ```

6. **Verify.** On a test server, add the repository URL, install the release from the catalog, restart, and open each tab after a hard reload.

## When a Release Is Wrong

* Never edit or reuse a published version. Fix it in a new bug release.
* When a release breaks servers, publish the fix as the next bug release at once.
* When the manifest entry itself is wrong, run **Publish Plugin** by hand with the release tag after fixing the cause.

## Checklist

* [ ] The version follows `JJ.JJ.F.B`, and later parts are `0`.
* [ ] Base is released and referenced, with local copies deleted.
* [ ] Stored schedules and exports from the last release still load or rebuild cleanly.
* [ ] `dotnet build`, `dotnet test` with jellyfin-ffmpeg, and `test-in-jellyfin.sh` pass on the released commit.
* [ ] Every tab was opened on a test server with the release zip.
* [ ] Docs, screenshots, Versioning, and How It Works describe this release.
* [ ] `mkdocs build --strict` passes.
* [ ] The changelog lists only this release, in the four keys.
* [ ] The release branch was merged into `master` before the version was set.
* [ ] The published draft matches `build.yaml` and has the zip and its `.md5`.
* [ ] The manifest commit's checksum matches the release's `.md5`.
* [ ] The manifest commit is pulled.
