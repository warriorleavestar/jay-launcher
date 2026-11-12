---
id: multiplayer
category: multiplayer
routes: play, play/*
related: child-mode, network-list, knock, connection, public-rooms, glossary
---

# Multiplayer

Play with friends: open a room and send your friend an invite; your friend pastes it and knocks, and comes in once you say yes.

![The multiplayer lobby: Join on top, then Open a room and Where to play](shot:multiplayer-lobby)

The Multiplayer page has no tabs: it is the lobby until you are in a room, and then it is the room.

## How to
- Join: paste the code you were given in the box at the top, click the button, then wait for a yes. Room invites, server invites, friend codes and network addresses all go in this one box, and the button says what it will do.
- Open a room: just click **Open room**; the four choices all have defaults. Who can come in (only people I invite, my friends can knock, public); how people connect (automatic, relays only, no server); protection (standard, stronger, highest); scope (just this game, whole LAN). An option that is greyed out says why underneath.
- Relays only: everything goes through relays and Jay connects nobody in the room directly, so in normal use they don't see each other's IP address. Someone running a modified program could still find it out. It is a little slower. A friend whose Jay is too old to know this setting is turned away and asked to update.
- No server: the button becomes **Make a sealed direct code**. Send the code, and tell the passphrase another way. The two computers connect straight to each other and never fall back to a relay. To find your address, Jay asks the STUN servers listed in multiplayer settings, and they see your IP address.
- Where to play: rooms your friends invited you to, public rooms and servers you saved are one list. Filter it with **All / Friends / Public / Servers**.
- Someone knocks: a card appears in your room; click **Allow** or **Decline** (Alt+Y / Alt+N).
- Safety code: if both of you see the same safety code, nobody is pretending to be someone else in between.
- The first time, multiplayer needs one extra download. The button reads "Install the multiplayer component first": click once to install it, then again to open the room or join.

## If something goes wrong
- Cannot connect: run "Check network"; it tells you which step fails.
- For a child: remote help, public networks and more can be turned off; see [For a child](help:child-mode).
