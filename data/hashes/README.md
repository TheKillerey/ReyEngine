# Hash dictionaries

ReyEngine resolves the obfuscated hashes inside `.wad.client` archives and `.bin`
files using community hash lists (CDTB / CommunityDragon format).

Drop the following files here and point ReyEngine at this folder
(`Tools ▸ Load Hash Dictionaries`):

| File                      | Resolves                                  | Hash         |
|---------------------------|-------------------------------------------|--------------|
| `hashes.game.txt`         | WAD chunk paths (Game client)             | XxHash64     |
| `hashes.lcu.txt`          | WAD chunk paths (LCU)                      | XxHash64     |
| `hashes.binentries.txt`   | `.bin` entry paths                         | FNV-1a 32    |
| `hashes.binfields.txt`    | `.bin` field names                         | FNV-1a 32    |
| `hashes.bintypes.txt`     | `.bin` class names                         | FNV-1a 32    |
| `hashes.binhashes.txt`    | `.bin` hashed string values                | FNV-1a 32    |

File format is one entry per line: `<hexhash> <path>`.

Source: https://github.com/CommunityDragon/CDTB  (the `cdragontoolbox` hash lists).
These files are intentionally git-ignored because they are large and update often.
