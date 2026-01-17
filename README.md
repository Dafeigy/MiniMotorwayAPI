## WIP

Wrapper for MiniMotorway. Currently doing some patch work.

## Known Information

### First thing first: Tile

Tile is the most fundamental object in this game. It plays an role of a basic  `block`  in the game, which can be the container of certain in-game  tool like Road, Traffic light, or the target like Destination. See `TilemapView` and `TilemapModel` for details.

### Type of Tile

- Carpark. Where the car is parked to get the passengers. Always show together with Destination.
- Destination. The "Producer" of the game that generates passengers continuously.
- Tree. Simple trees ^_^
- House. Where the car is stored.
- None. None is a road-like tile generated along with Destination & Carpark, or User's operation.

### Way to get info

It's about "When" and "What". You can update the information by setting a timer, or patch the function which is triggered by events like, player's operation, Destination spawn, etc.

### Color Mapping

Destination Type(identified by color)  in the game is called `GroupIndex`. I run some test to see the actual mapping:

- 0:<font style="color: #FEC980">Yellow</font>
- 1: <font style="color: #EF3F3C">Red</font>
- 2:<font style="color: #AAE8F1">Cyan</font>
- 3:<font style="color: #6E95C3">Navy</font>
- 4:<font style="color: #8AE49C">Green</font>
- 5:Maybe later I will do.

## Structure

I am doing this later.