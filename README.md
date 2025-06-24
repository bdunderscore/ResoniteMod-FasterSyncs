# FasterSyncs

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that modifies the way that Resonite syncs assets and records to the cloud. it addresses Yellow-Dog-Man/Resonite-Issues/#3994.
By checking for the status of pre-existing meshes in parallel, this can greatly speed up saving worlds with large
numbers of assets, particularly when you are located somewhere with a high-latency connection to the Resonite API servers.

## Installation

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Place [FasterSyncs.dll](https://github.com/bdunderscore/ResoniteMod-FasterSyncs/releases/latest/download/FasterSyncs.dll) into your `rml_mods` folder. This folder should be at `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods` for a default install. You can create it if it's missing, or if you launch the game once with ResoniteModLoader installed it will create the folder for you.

## Credits

- [hazre's VRCFTReceiver](https://github.com/hazre/VRCFTReceiver/), which I used as a starting point for this mod
