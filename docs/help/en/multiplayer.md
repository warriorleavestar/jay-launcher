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
- No server: the button becomes **Make a sealed direct code**. Send the code, and tell the passphrase another way. The two computers connect straight to each other and never fall back to a relay. To find your address, Jay asks the STUN servers listed in Settings › Network, and they see your IP address.
- Where to play: rooms your friends invited you to, public rooms and servers you saved are one list. Filter it with **All / Friends / Public / Servers**.
- Someone knocks: a card appears in your room; click **Allow** or **Decline** (Alt+Y / Alt+N).
- Safety code: if both of you see the same safety code, nobody is pretending to be someone else in between.
- The first time, multiplayer needs one extra download. The button reads "Install the multiplayer component first": click once to install it, then again to open the room or join.

## In the room
- The top shows the room's name, how many people are in it, and the connection mark ("Direct · 18 ms" or "Via relay · 82 ms"). A thin line under each face says whether that person is reached directly or through a relay. Friends you have verified have a tick on their face.
- **Invite**: pick how long it is valid (10 minutes, 1 day, 30 days) and how many uses (once, no limit), then click **Create invite**. The invite is a small card: **Copy** it or **Send to a friend**; the code itself only appears when you click **Show**. An invite with no use limit always asks you before anyone comes in. A 30-day invite works in every room you open later (in the room that is open now only when it has no other invite out; the card says which), and you can revoke it at any time.
- Click the connection mark for "Who can see what": a fixed table of who sees your IP address and who sees the content. The row that applies to you right now is marked "Now". Under it is the switch "Relays only": Jay asks the others not to connect to you directly, so in normal use they don't see your IP (someone running a modified program could still find it out), and latency is somewhat higher; changing it reconnects you to the room. With the switch off, people in the room can find out your IP even while the link happens to go through a relay, because Jay keeps trying to connect directly. To have it on in every room, turn on "Always use relays only" in Settings › Multiplayer.
- Click a person to see their safety code, whether they are a friend, and how you are connected. "Share with them" holds remote help, forwarding their game, and letting them get online through you; each one asks you first.
- **Enter the game**: someone who joined clicks it to go straight into the friend's world with the instance they picked.

## If something goes wrong
- Cannot connect: run "Check network"; it tells you which step fails.
- For a child: remote help, public networks and more can be turned off; see [For a child](help:child-mode).
