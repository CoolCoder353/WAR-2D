def CountTilesLeft(x, y, grid):
    count = 0
    while x >= 0 and grid[y][x] == 1:
        count += 1
        x -= 1
    return count

def CountTilesRight(x, y, grid):
    count = 0
    while x < len(grid[0]) and grid[y][x] == 1:
        count += 1
        x += 1
    return count

def CountTilesUp(x, y, grid):
    count = 0
    while y >= 0 and grid[y][x] == 1:
        count += 1
        y -= 1
    return count

def CountTilesDown(x, y, grid):
    count = 0
    while y < len(grid) and grid[y][x] == 1:
        count += 1
        y += 1
    return count

def CountTiles(x, y, grid):
    return [CountTilesLeft(x, y, grid), CountTilesRight(x, y, grid), CountTilesUp(x, y, grid), CountTilesDown(x, y, grid)]

def FindSmallestLength(x, y, grid):
    return min(CountTiles(x, y, grid))

def ReplaceWallsAndSquares(grid):
    new_grid = [['x' if cell == 0 else 0 for cell in row] for row in grid]
    counter = 1
    for y in range(len(grid)):
        for x in range(len(grid[0])):
            if grid[y][x] == 1 and new_grid[y][x] == 0:
                length = FindSmallestLength(x, y, grid)
                for i in range(length):
                    for j in range(length):
                        if new_grid[y + i][x + j] == 0:
                            new_grid[y + i][x + j] = counter
                counter += 1
    return new_grid

# Input tile map
input_grid = [
    [1, 0, 1, 1],
    [1, 1, 1, 0],
    [1, 1, 1, 1],
    [0, 0, 1, 1]
]

# Replace walls and squares
output_grid = ReplaceWallsAndSquares(input_grid)

# Print the new grid
for row in output_grid:
    print(' '.join(map(str, row)))