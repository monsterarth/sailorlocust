# Sailor Locust

A tamed locust in a sailor's hat that takes the helm of a boat and sails it between named docks, for Vintage Story 1.22.

- **Dock post** (`sailordock`): a block with a name. Boats stop there.
- **Route buoy** (`sailorbuoy`): placed on the water, with a name. Boats told to pass by it do so without stopping.
- **Sailor locust** (`sailorlocust`): use the item on a boat and it stands by the helm. Right click the helm seat (or
  press `O` on board or in the water near it) for its sailing orders: click the docks and buoys it knows to add stops,
  reorder them, set the wait at each dock. After the last stop it starts over. Sneak and right click to take it back.
- **Route wand** (`routewand`, creative only): shows the way a locust has planned.

## Ports

Docks with the same name are the berths of one port. A boat asks for a berth as it nears the port and waits off the port
when none is free (first come, first served). A berth is only given to a boat whose hull fits along the pier and that has
a clear way in; a dock can be kept for small boats only. Coming in or leaving, a boat keeps the water it works in, and
the others wait their turn. A long boat comes in along the pier and is tied up alongside; it leaves astern along the pier
when it has no room to turn.

A buoy named after a port can be given a role in its sheet: waiting area (blue beacon), way out (green), way in (yellow,
passed by every boat bound for the port).

No world map is needed: the route is made of docks and buoys placed in the world.

## How it finds its way

1. **The long route.** World generation's ocean map gives, for any point of the world and with no chunk generated, how
   much sea there is (one value for each 32 blocks). A* over it gives the way around the land. It does not see lakes,
   rivers or islets.
2. **The water in sight.** Around the boat, 81 x 81 real blocks at the water's level, looked at again every 1.2 s, and
   A* through them towards the next point of the long route. This is what keeps it off shores, rocks and ice.
3. **Buoys.** Where the two above are not enough (out of a harbour, through a channel, up a river), a buoy in the orders
   is a point the boat must pass by.

The locust does not sit in the commanding seat: with a rider there the server leaves the boat's movement to that
rider's client. It stands by the helm and sets the boat's own forward speed and turning speed to what a player's keys
would give on that boat, so each boat keeps its own speed and turning; the sail is set too, on boats that have one.
If a player takes the helm, the locust stands by until they leave it. It works on the game's boats and on any boat
built the same way (an entity with seats, one of them commanding, and the `ForwardSpeed` and `AngularVelocity` fields);
tried on the game's sailboat and raft and on Shipwright's catboat. The server only runs the physics of entities a
player is following; while nobody follows the boat, the mod gives the boat its physics steps itself.

## Out of sight

An entity is only moved while its chunk is loaded. A boat under orders whose chunk unloads, or that reaches the edge of
the loaded world with no player near, leaves the world: a copy of the boat with all it carries goes into the savegame
and it advances by time along its route, at the speed measured while it really sailed. When the place where it is by
now is loaded, the boat is put back on the water there with its locust at the helm. A boat with a player seated in it
never leaves the world. `OffscreenSailing: false` in the config turns this off (the boat then waits where it is).

## Config (`ModConfig/sailorlocust.json`)

| Key | Default | |
|---|---|---|
| `EnableRecipes` | `true` | `false` removes the three grid recipes |
| `OceanThreshold` | `40` | ocean map value from which a point counts as sea for the long route |
| `ArrivalRadius` | `7` | blocks from a dock at which the boat has arrived |
| `BuoyPassRadius` | `9` | blocks from a buoy at which it is passed |
| `DefaultWaitSeconds` | `60` | wait at a dock when the orders give none |
| `CruiseSail` | `2` | sail on open water (0 furled, 1 half, 2 full) |
| `OwnerOnly` | `false` | only who placed a locust may change its orders or take it back |
| `OffscreenSailing` | `true` | sail on by time while nobody is near |
| `BerthAskDistance` | `96` | blocks from a port at which a boat asks for a berth |
| `HideUnseen` | `true` | a boat with no player within `UnseenDistance` leaves the world and sails on by time |
| `UnseenDistance` | `160` | blocks; it comes back on the water when a player comes within this minus 40 |
| `Debug` | `false` | logs the pilot's decisions |

Staff commands (privilege `controlserver`): `/sailor list`, `/sailor fleet`, `/sailor route <from> <to>`. Staff also
see every boat under orders on the world map (layer "Sailor Locust: boats").

## Credits

Inspired by the **Conductor Locust** of *Yang's Transport Tycoon* (a locust that drives trains between named stations on
a timetable, and trains that go on off screen) and by **Boat Autopilot** by Majke (a boat that follows a route by
itself). The ideas only: this mod shares no code and no assets with either. The locust's body and its skin are the
game's own, used by reference; the hat, the dock post and the buoy are this mod's.

## Building and testing

`build.ps1` compiles against the game's .NET 10 runtime and packs `dist\sailorlocust_<version>.zip`.
`tests\ingame\run.ps1` runs a dedicated server with nobody on it and lets boats sail a circuit around land
(`-Extra offscreen` for the test of sailing out of sight).
