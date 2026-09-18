# Persistent NPC crowd

The default crowd contains 20 locally owned NPCs, in addition to up to 400 viewers.
`PlayerManager` allocates 432 floor slots (24 columns by 18 rows), preserves NPCs
on viewer joins, capacity eviction, viewer expiry and reconnect snapshots, and
creates a fresh crowd on explicit reset. Incoming events cannot create or rename
NPC identities. NPCs remain excluded from welcome cards and top donor ranks.

`NpcNamePool` shuffles 1,520 names (80 nicknames and 24 family names × 60 given
names). It exhausts the deck before reshuffling and skips names already occupied
by another NPC. Names stay fixed for each NPC's lifetime. Character art and floor
placement continue using the existing random selection.

Run the Windows player with a rendering desktop:

```text
TikTokLiveGameUnity.exe -welcomePreviewPath <output-directory> -npcPreview
```

This preview omits the live WebSocket transport. It checks 20 distinct NPCs at
startup, the full name deck, joins, reconnects (including duplicate/invalid roster
entries), all 400 viewer slots plus 20 NPC slots, overflow eviction, expiry,
oversized snapshots and reset. Screenshots and `npc-verification.txt` are written
to the output directory. `npc-names.txt` records the initial random names.

The normal `-welcomePreviewPath` mode still runs the welcome-card regression
capture against the persistent crowd.
