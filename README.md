# fast Timeline — Koikatu CharaStudio

A performance-focused fork of Timeline 1.5.6, targeting animation playback and Timeline track selection, scrolling, and zooming.

**This repository currently targets Koikatu KK / CharaStudio only.** 

## Download and installation

Check [Releases](https://github.com/OQ-J/fast-timeline/releases) for compiled packages. If no release is available yet, build from source or download the build artifact from a successful GitHub Actions run.

1. Close CharaStudio. Back up your existing Timeline DLL and move the old Timeline implementation outside the plugins directory.
2. Extract the package into your game directory so the plugin is located at `BepInEx/plugins/fast_timeline.dll`.
3. Ensure the old `Timeline.dll` and new `fast_timeline.dll` are not installed together as two Timeline implementations. Separate Timeline extension plugins can remain installed.
4. Start CharaStudio and use Timeline as usual.

Dependencies follow upstream Timeline 1.5.6: BepInEx 5, BepisPlugins / ExtendedSave, and KKAPI. Install third-party track plugins according to their own requirements. The game tests used KKAPI 1.48.1; this project references KKAPI 1.43.0 for compilation. Renaming the DLL does not change Timeline's plugin GUID or assembly name.

## Retained optimizations

- Playback: key-time lookup, PlaybackTreeCache, and Segment Cursor reduce repeated searches and traversal.
- UI: virtualization, display-object pooling, event binding, and display updates for track selection, scrolling, and zooming.
- Compatibility: the original XML save format, public API, third-party XML callbacks, exception and partial-load behavior, and required animation-state restoration are preserved.

## Historical in-game benchmarks

**Version scope: the results below compare upstream Timeline 1.5.6 with fast Stage3g, before the save/load rollback. They are not a new benchmark of the current playback-and-UI-only DLL.** The current version retains the tested playback/UI source. Its rebuilt DLL is byte-identical to the existing Stage2 milestone, but the historical percentages must not be presented as new measurements of the current DLL.

Environment: real CharaStudio, Unity 5.6.2f1, Mono 2.0.50727.1433, 64-bit Windows, and a 2560×1440 display. Tests used real scenes and normal third-party callbacks, rather than Unity substitutes.

### Track selection: two cards

Ordinary left-click selection was dispatched through the real Unity PointerDown handlers, measuring synchronous CPU time. Each card/version used one separate process: 12 warm-up clicks, then 3 rounds of 30 clicks, plus 90 no-click controls. The three rounds share the same process. This does not measure end-to-end mouse-to-display latency.

| Card | Tracks / Keys | Original click ms | fast click ms | Change | Active track display objects: original → fast |
|---|---:|---:|---:|---:|---:|
| 1 | 192 / 9790 | 8.7928 | 0.7067 | -92.0% | 191 → 11 |
| 8 | 27 / 316 | 1.1606 | 0.5599 | -51.8% | 27 → 11 |

### Animation playback CPU: 13 cards

Measurements sum the real `InterpolateBefore` and `InterpolateAfter` root-method times, including normal third-party interpolation and the Unity work it invokes. They do not represent whole-frame time. Each card/version used a separate process. Windows starting at 10%, 50%, and 90% of the animation each received at least 5 seconds of warm-up and 10 seconds of sampling. Values below are the mean of the three window medians, in milliseconds. Open/closed refers to the Timeline panel.

| Card | Tracks | Keys | Panel open: original → fast ms (change) | Panel closed: original → fast ms (change) |
|---|---:|---:|---:|---:|
| 1 | 192 | 9790 | 1.374 → 0.821 (-40.3%) | 1.018 → 0.749 (-26.4%) |
| 2 | 229 | 6472 | 1.456 → 1.172 (-19.5%) | 1.376 → 1.212 (-11.9%) |
| 3 | 63 | 3345 | 0.571 → 0.493 (-13.7%) | 0.408 → 0.286 (-29.9%) |
| 4 | 147 | 9358 | 1.501 → 1.111 (-26.0%) | 1.218 → 1.034 (-15.1%) |
| 5 | 102 | 2316 | 1.026 → 1.012 (-1.4%) | 0.961 → 0.910 (-5.3%) |
| 6 | 249 | 16015 | 2.374 → 1.488 (-37.3%) | 1.808 → 1.401 (-22.5%) |
| 7 | 250 | 13307 | 1.827 → 1.237 (-32.3%) | 1.342 → 1.108 (-17.5%) |
| 8 | 27 | 316 | 0.495 → 0.487 (-1.5%) | 0.486 → 0.371 (-23.8%) |
| 9 | 213 | 17791 | 2.431 → 1.001 (-58.8%) | 1.483 → 0.938 (-36.7%) |
| 10 | 132 | 5746 | 1.219 → 0.992 (-18.6%) | 1.045 → 0.754 (-27.9%) |
| 11 | 179 | 13055 | 2.207 → 1.316 (-40.3%) | 1.517 → 1.285 (-15.3%) |
| 12 | 144 | 1571 | 1.044 → 1.073 (+2.7%) | 1.083 → 1.001 (-7.6%) |
| 13 | 180 | 2082 | 1.286 → 1.323 (+2.9%) | 1.292 → 1.111 (-14.0%) |

### Scrolling and zooming: 13 cards

Vertical/horizontal scrolling values are median whole-frame intervals during the test. They include the game, other plugins, rendering, and waiting; they are not Timeline-exclusive CPU measurements or a basis for FPS claims. Zoom values are median synchronous CPU times for 20 alternating ZoomIn/ZoomOut operations starting at zoom=2. Both versions received the same 20-step input sequence.

| Card | Vertical scroll frame interval: original → fast ms | Horizontal scroll frame interval: original → fast ms | Zoom CPU: original → fast ms (change) |
|---|---:|---:|---:|
| 1 | 70.985 → 33.362 | 93.636 → 34.349 | 184.321 → 18.712 (-89.8%) |
| 2 | 42.543 → 28.414 | 57.544 → 27.571 | 152.267 → 5.056 (-96.7%) |
| 3 | 28.234 → 26.950 | 34.537 → 25.890 | 39.661 → 13.630 (-65.6%) |
| 4 | 66.587 → 30.157 | 89.406 → 29.611 | 167.637 → 12.780 (-92.4%) |
| 5 | 36.874 → 36.719 | 38.545 → 37.783 | 31.066 → 4.614 (-85.1%) |
| 6 | 113.768 → 29.527 | 141.616 → 31.354 | 368.723 → 12.674 (-96.6%) |
| 7 | 90.099 → 35.480 | 125.276 → 37.073 | 313.195 → 17.731 (-94.3%) |
| 8 | 24.555 → 24.065 | 24.113 → 25.040 | 2.750 → 3.405 (+23.8%) |
| 9 | 106.663 → 30.023 | 158.514 → 29.663 | 359.194 → 15.636 (-95.6%) |
| 10 | 38.176 → 26.090 | 55.168 → 23.601 | 95.005 → 10.456 (-89.0%) |
| 11 | 74.729 → 25.007 | 105.878 → 25.472 | 241.825 → 13.748 (-94.3%) |
| 12 | 26.667 → 25.794 | 26.701 → 24.699 | 26.442 → 3.372 (-87.2%) |
| 13 | 37.742 → 38.407 | 39.347 → 38.948 | 44.540 → 4.195 (-90.6%) |


## Building from source

Only the KK project and its three shared-code directories are retained:

```text
Timeline.KK/       KK project entry point
Timeline.Core/     Timeline, playback, and UI optimizations
ToolBox/           Shared utilities
UIUtility/         UI components and embedded resources
fast-timeline.sln  Solution containing the KK project
```

Install the .NET 8 SDK (the Runtime alone is insufficient), then run from the repository root:

```powershell
dotnet restore fast-timeline.sln
dotnet build fast-timeline.sln --configuration Release --no-restore
./release.ps1
```

The target remains .NET Framework 3.5. NuGet restores compilation dependencies and reference assemblies; do not commit your game DLLs. The DLL is written to `bin/build/Timeline.KK/Timeline.dll`. Packaging creates `artifacts/fast_timeline_v1.5.6.0.zip`, containing `BepInEx/plugins/fast_timeline.dll`, the complete `LICENSE`, and this README.

The existing development workspace can also use its original build tools outside this repository. SDK/CI builds require the environment above. GitHub Actions builds only KK and retains the ZIP as an artifact; check the corresponding Actions run for its actual status.

## Credits and license

- Original HSPlugins / Timeline author: **Joan6694**. Original project: [HSPlugins](https://bitbucket.org/Joan6694/hsplugins/src/master/).
- This fork is based on Timeline 1.5.6 from [IllusionMods/HSPlugins](https://github.com/IllusionMods/HSPlugins).
- The upstream MIT license and copyright notice are retained: `Copyright (c) 2021 DeathWeasel1337`. See the complete [LICENSE](LICENSE); the upstream copyright line has not been replaced with this repository owner's name.
- This repository maintains playback/UI performance changes and documentation. It is not an official upstream release. Include the MIT license when redistributing the DLL.
