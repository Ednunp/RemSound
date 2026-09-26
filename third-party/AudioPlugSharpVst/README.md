# AudioPlugSharpVst.vst3 - RemSound's patched copy

The plugin's native VST3 loader. It comes from AudioPlugSharp (Mike Oliphant, MIT licence,
https://github.com/mikeoliphant/AudioPlugSharp), with one change: `infinite-tail.patch` makes the processor
report `kInfiniteTail` from `getTailSamples()`. The stock loader inherits the SDK default, `kNoTail`, and the
library gives a C# plugin no way to change it.

Why: a receiving RemSound makes sound with nothing coming in. Hosts that decide from the tail whether to keep
calling an effect on a silent track (Cubase, Bitwig and Waveform, per their documentation and source) stop
calling one that reports no tail, and the person on that track drops out. Measured on 24 September 2026 in
REAPER 7.80: the tail makes no difference there. REAPER keeps calling an effect on an empty track anyway, and
its own dropout is "Close audio device when stopped and application is inactive" (see
archive/research/DAW-instrument-vs-effect-2026-09-24.md).

`RemSound.Plugin.csproj` copies this file over the package's own loader after the package has copied it, as
`RemSound.PluginBridge.vst3`. The gate checks the shipped loader contains the override.

## It must match the AudioPlugSharp package version

Built from commit `7a84d73` ("Update to VS2026, .NET 10"), the source of the 0.7.13 NuGet packages. The
package's nuspec names `d725e20`, which is older; between the two only the project files changed (.NET 10,
VS 2026). The unpatched rebuild is the same size as the packaged file, 922112 bytes.

If the AudioPlugSharp / AudioPlugSharpVst3 package version ever changes, rebuild this file from the matching
commit. The build stops with an error until you do.

## Rebuilding

Needs Visual Studio 2026 with the C++ desktop workload and "C++/CLI support" (Microsoft.VisualStudio.Component.VC.CLI.Support).

    git clone https://github.com/mikeoliphant/AudioPlugSharp
    cd AudioPlugSharp
    git checkout 7a84d73
    git submodule update --init vst3sdk
    cd vst3sdk && git submodule update --init base pluginterfaces public.sdk cmake && cd ..
    git apply <this folder>/infinite-tail.patch
    cd vstbuild
    cmake -G "Visual Studio 18 2026" -A x64 ../vst3sdk -DSMTG_CREATE_PLUGIN_LINK=0 -DSMTG_ENABLE_VSTGUI_SUPPORT=OFF -DSMTG_ENABLE_VST3_PLUGIN_EXAMPLES=OFF -DSMTG_ENABLE_VST3_HOSTING_EXAMPLES=OFF
    cd ..
    msbuild AudioPlugSharp.sln -t:AudioPlugSharpVst -p:Configuration=Release -p:Platform=x64

The result is `x64\Release\AudioPlugSharpVst.vst3`. Build through the solution, not the .vcxproj on its own:
building the project with Platform=x64 pushes x64 onto the C# library, whose unsafe-code setting is only on for
AnyCPU. CMake ships with Visual Studio (Common7\IDE\CommonExtensions\Microsoft\CMake). A working clone is at
D:\proj\AudioPlugSharp on Ed's desktop.
