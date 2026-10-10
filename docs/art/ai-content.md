# AI-generated content log

Every asset made with an AI image tool, for the Steam content survey (roadmap v0.9; spec §7.5). The raw output and its prompt are kept next to each other in the repo. Assets deleted later stay in this log with their status.

| Asset | Raw output and prompt | Tool and model | Date | Used for | Status |
|---|---|---|---|---|---|
| Tank (top-down, facing east) | `Assets/Art/Prototype/b/raw/Tank.png`, `Tank.prompt.txt` | Figma `generate_image`, gemini-3.1-flash-image | 2026-10-11 | Method prototype (b): cleaned to `b/Tank.png` (16×16, prototype palette) | Prototype only |
| Miner (top-down, drill east) | `b/raw/Miner.png`, `Miner.prompt.txt` | Figma `generate_image`, gemini-3.1-flash-image | 2026-10-11 | Method prototype (b): `b/Miner.png` | Prototype only |
| Terrain set (floor, rock, gem rock, bedrock) | `b/raw/Terrain.png`, `Terrain.prompt.txt` | Figma `generate_image`, gemini-3.1-flash-image | 2026-10-11 | Method prototype (b): `b/Ground.png`, `Wall.png`, `Gem.png`, `Border.png` | Prototype only |

Cleanup for all three: `WAR-2D/Art/Clean AI prototypes` (`Editor/ArtMenu.CleanAiPrototypes`) keys out the requested green background, crops each subject to a square, and quantizes it to 16×16 in the prototype palette (`PaletteQuantizer`). The credits came from the owner's Figma plan "IFB103 Project", chosen by the owner.
