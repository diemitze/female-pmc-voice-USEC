# Changelog

## 1.0.5

### Fixed
- Female appearance completion never ran. On startup the server logged "Neither W.A.A.C.
  nor DLC.FemaleClothes found" even with both installed, because it looked for them in the
  bot pools, where neither mod adds anything. It now checks the customization table, on the
  first generated bot, so load order no longer matters.
- Bots no longer end up with a male head on a female body. Appearance completion now needs
  W.A.A.C. (the only source of female heads) and stays off without it. DLC.FemaleClothes is
  optional and adds the random USEC outfits on top.
- A very low `botVoiceChancePercent` could round the voice weight down to 0, so the voice
  never spawned. The weight is now at least 1.
- The config is found even if the mod folder is renamed.

### Improved
- Client: if a game update breaks one patch, it is skipped on its own with a warning instead
  of stopping the other patches from loading.
- Client: less CPU and garbage in busy raids. Reflection lookups for the breathing and pain
  checks are now cached instead of repeated for every player every tick.
- Server: error logs from the appearance patch now include the full stack trace.
