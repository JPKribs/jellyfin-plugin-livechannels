# Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
12.4.0.0
JJ JJ F B
```

* `JJ.JJ` is the Jellyfin version this build was tested and released for.
* `F` is the plugin feature release.
* `B` is the plugin bug or patch release within that feature.

Targets **Jellyfin 12.4.x** (`net10.0`, ABI `12.4.0.0`). Requires ffmpeg, which Jellyfin already bundles and configures.
