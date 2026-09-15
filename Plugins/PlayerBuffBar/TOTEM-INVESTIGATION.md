# Totem tracking investigation

The per-bar Show Totems toggle and grouped counts are implemented. The full
Release solution build passed. Countdown support is unfinished: no lifetime
offset has been verified. Do not substitute elapsed wall-clock time for game time.

Live sample supplied by the user (PoE2 0.5.5b):

- Entity ID: 2813, address: `0x3753783BA80`.
- Metadata: `Metadata/Monsters/Totems/SpellTotem@34`.
- Model: `Metadata/Monsters/Totems/DruidicTotem/DruidicTotem4.ao`.
- DiesAfterTime: `0x374464934B0`; owner at +0x08 matches the entity.
- DiesAfterTime vtable: `0x7FF6BF547A30` (runtime address, requires rebasing).
- +0x20 points to `0x374E8999700`; +0x40 points to `0x374464936D0`.
- Spell Totem icon verified from its PoE2DB page:
  `skillicons/4k/druidspelltotem.webp` under the existing CDN art base.

Captured 512 bytes at the entity, DiesAfterTime, and the +0x40 target before
and after the user briefly unpaused. The entity and DiesAfterTime snapshots
were identical. Only +0x130 and later of the +0x40 target changed; these look
like adjacent allocations, not a verified countdown. Entity ID and valid flag
still match after the comparison. These reads do not establish object sizes.

Next: inspect the current executable's DiesAfterTime implementation to locate
its lifetime/expiry storage and game clock, then validate live. Once there is
a verified remaining time, show the earliest expiry for each grouped type so
the countdown predicts when its displayed count will decrease. Verify ownership,
multiple types, despawn, pause, and area transitions in-game before completion.
