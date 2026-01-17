## WIP

Wrapper for MiniMotorway. Currently doing some patch work.

Currently:

1. Tile Information: Coordinates, Tile type, world & screen coordination.
2. (WIP) User Operation: Add Road, Motorways, etc,.

## Known Information

### First thing first: Tile

Tile is the most fundamental object in this game. It plays an role of a basic  `block`  in the game, which can be the container of certain in-game  tool like Road, Traffic light, or the target like Destination. See `TilemapView` and `TilemapModel` for details.

### Type of Tile

- `Carpark`. The place where the car is parked to get the passengers. Always shown together with Destination.
- `Destination`. The "Producer" of the game that generates passengers continuously.
- `Tree`. Simple trees ^_^
- `House`. Where the car is stored.
- **None**. None is a road-like tile generated along with` Destination` & `Carpark`, or User's operation.

### Way to get info

It's about "When" and "What". You can update the information by setting a timer, or patch the function which is triggered by events like, player's operation, Destination spawn, etc.

### Color Mapping（Need to be verified）

Destination Type(identified by color)  in the game is called `GroupIndex`. I run some test to see the actual mapping:

<table>  <thead>    <tr>      <th>Group Index</th>      <th>Color</th>      <th>Example</th>    </tr>  </thead>  <tbody>
    <tr><td>0</td><td>#EF3F3C</td><td><p style="color: #EF3F3C">■</p></td></tr>
	<tr><td>1</td><td>#FEC980</td><td><p style="color: #FEC980">■</p></td></tr>
	<tr><td>2</td><td>#AAE8F1</td><td><p style="color: #AAE8F1">■</p></td></tr>
	<tr><td>3</td><td>#6E95C3<br/>#D1CFCB</td><td><p style="color: #6E95C3">■</p><p style="color: #D1CFCB">■</p></td></tr>
    <tr><td>4</td><td>#8AE49C</td><td><p style="color: #8AE49C">■</p></td></tr>
    <tr><td>5</td><td>TODO...</td><td><p>■</p></td></tr>
</tbody></table>

Here shows the Example of Color Mapping:

![color map example](/imgs/colormap.png)

I am not sure if this color mapping is a constant one, since I found `GroupIndex == 3` could be either Gray or Navy Blue in city Beijing and Los Angeles, respectfully.

## Plugin Structure

I am doing this later.