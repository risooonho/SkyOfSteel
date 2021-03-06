using Godot;
using Optional.Unsafe;
using System;
using System.Linq;
using System.Collections.Generic;
using static Godot.Mathf;
using static SteelMath;


public class World : Node {
	public const float DayNightMinutes = 30f;
	public const int PlatformSize = 12;
	public const int ChunkSize = 9 * PlatformSize;

	public static Dictionary<Items.ID, PackedScene> Scenes = new Dictionary<Items.ID, PackedScene>();

	public static float TimeOfDay { get; private set; } = 15f * DayNightMinutes;

	public static Dictionary<Tuple<int, int>, ChunkClass> Chunks = new Dictionary<Tuple<int, int>, ChunkClass>();
	public static Dictionary<int, List<Tuple<int, int>>> RemoteLoadedChunks = new Dictionary<int, List<Tuple<int, int>>>();
	public static Dictionary<int, List<Tuple<int, int>>> RemoteLoadingChunks = new Dictionary<int, List<Tuple<int, int>>>();
	public static Dictionary<int, int> ChunkRenderDistances = new Dictionary<int, int>();
	public static List<DroppedItem> ItemList = new List<DroppedItem>();
	public static GridClass Grid = new GridClass();
	public static Pathfinding Pathfinder = new Pathfinding();

	public static bool IsOpen = false;
	public static string SaveName = null;

	public static Node EntitiesRoot = null;
	public static ProceduralSky WorldSky = null;
	public static Godot.Environment WorldEnv = null;

	private static PackedScene DroppedItemScene;
	private static PackedScene DebugPlotPointScene;

	public static World Self;

	World() {
		if(Engine.EditorHint) { return; }

		Self = this;

		DroppedItemScene = GD.Load<PackedScene>("res://Items/DroppedItem.tscn");
		DebugPlotPointScene = GD.Load<PackedScene>("res://World/DebugPlotPoint.tscn");

		var ErrorScene = GD.Load<PackedScene>("res://World/Scenes/ERROR.tscn");

		foreach(Items.ID Type in Enum.GetValues(typeof(Items.ID))) {
			if(Type == Items.ID.NONE) continue;

			File ToLoad = new File();
			if(ToLoad.FileExists($"res://World/Scenes/{Type}.tscn")) {
				var Scene = GD.Load<PackedScene>($"res://World/Scenes/{Type}.tscn");
				if(!(Scene.Instance() is Tile))
					throw new System.Exception($"Tile scene for '{Type}' does not inherit Structure");
				else
					Scenes.Add(Type, Scene);
			}
			else
				Scenes.Add(Type, ErrorScene);
		}
	}


	public override void _Ready() {
		WorldEnv = GetTree().Root.GetNode<WorldEnvironment>("RuntimeRoot/WorldEnvironment").Environment;
		WorldSky = WorldEnv.BackgroundSky as ProceduralSky;
	}


	public static void DebugPlot(Vector3 Position, float MaxLife = 0) {
		var Point = (DebugPlotPoint)DebugPlotPointScene.Instance();
		Point.MaxLife = MaxLife;
		EntitiesRoot.AddChild(Point);
		Point.Translation = Position;
	}


	public static void DefaultPlatforms() {
		string GuidName = System.Guid.NewGuid().ToString();
		var BranchType = Items.ID.PLATFORM;
		var Position = new Vector3();
		var Rotation = new Vector3();
		Self.PlaceWithName(BranchType, Position, Rotation, 0, GuidName);
		Net.SteelRpc(Self, nameof(PlaceWithName), BranchType, Position, Rotation, 0, GuidName);
	}


	public static void Start() {
		Close();
		Menu.Close();

		Items.SetupItems();

		Node SkyScene = ((PackedScene)GD.Load("res://World/SkyScene.tscn")).Instance();
		SkyScene.Name = "SkyScene";
		Game.RuntimeRoot.AddChild(SkyScene);

		SkyScene.AddChild(new Starfield());

		EntitiesRoot = new Node();
		EntitiesRoot.Name = "EntitiesRoot";
		SkyScene.AddChild(EntitiesRoot);

		TimeOfDay = DayNightMinutes * 60 / 4;
		IsOpen = true;
	}


	public static void Close() {
		if(Game.RuntimeRoot.HasNode("SkyScene")) {
			Game.RuntimeRoot.GetNode("SkyScene").Free();
			//Free instead of QueueFree to prevent crash when starting new world in same frame
		}

		//This is NOT a leak! Their parent was just freed ^
		EntitiesRoot = null;

		Net.Players.Clear();
		Game.PossessedPlayer = Player.None();

		Pathfinder.Clear();
		Chunks.Clear();
		RemoteLoadedChunks.Clear();
		ItemList.Clear();
		Grid.Clear();

		Items.IdInfos.Clear();

		SaveName = null;
		IsOpen = false;
	}


	public static void Clear() {
		List<Tile> Branches = new List<Tile>();
		foreach(KeyValuePair<Tuple<int, int>, ChunkClass> Chunk in Chunks) {
			foreach(Tile Branch in Chunk.Value.Tiles) {
				Branches.Add(Branch);
			}
		}
		foreach(Tile Branch in Branches) {
			World.Self.RemoveTile(Branch.Name);
		}

		DroppedItem[] RemovingItems = new DroppedItem[ItemList.Count];
		ItemList.CopyTo(RemovingItems);
		foreach(DroppedItem Item in RemovingItems) {
			Item.Remove();
		}

		foreach(Node Entity in EntitiesRoot.GetChildren()) {
			if(Entity is Player)
				continue;

			Entity.QueueFree();
		}

		Pathfinder.Clear();
		Chunks.Clear();
		Grid.Clear();

		foreach(KeyValuePair<int, List<Tuple<int, int>>> Pair in RemoteLoadedChunks) {
			RemoteLoadedChunks[Pair.Key].Clear();
		}
	}


	[Remote]
	public void RequestClear() {
		Clear();
	}


	public static void Save(string SaveNameArg) {
		Directory SaveDir = new Directory();
		if(SaveDir.DirExists($"user://Saves/{SaveNameArg}"))
			System.IO.Directory.Delete($"{OS.GetUserDataDir()}/Saves/{SaveNameArg}", true);
		SaveDir.MakeDirRecursive($"user://Saves/{SaveNameArg}/Chunks");

		SavedMeta Meta = new SavedMeta(TimeOfDay);
		string SerializedMeta = Newtonsoft.Json.JsonConvert.SerializeObject(Meta, Newtonsoft.Json.Formatting.Indented);
		System.IO.File.WriteAllText($"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Meta.json", SerializedMeta);

		int SaveCount = 0;
		foreach(KeyValuePair<System.Tuple<int, int>, ChunkClass> Chunk in Chunks) {
			SaveCount += SaveChunk(Chunk.Key, SaveNameArg);
		}
		Console.Log($"Saved {SaveCount.ToString()} structures to save '{SaveNameArg}'");
	}


	public static bool Load(string SaveNameArg) {
		if(string.IsNullOrEmpty(SaveNameArg) || string.IsNullOrWhiteSpace(SaveNameArg))
			throw new Exception("Invalid save name passed to World.Load");
		if(!IsOpen)
			throw new Exception("The world must be open to load a savefile");

		Directory SaveDir = new Directory();
		if(SaveDir.DirExists($"user://Saves/{SaveNameArg}")) {
			string MetaPath = $"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Meta.json";
			if(SaveDir.FileExists(MetaPath)) {
				string SerializedMeta = System.IO.File.ReadAllText(MetaPath);
				SavedMeta Meta = Newtonsoft.Json.JsonConvert.DeserializeObject<SavedMeta>(SerializedMeta);

				TimeOfDay = Clamp(Meta.TimeOfDay, 0, 60f * DayNightMinutes);
			}

			Clear();
			Net.SteelRpc(Self, nameof(RequestClear));
			DefaultPlatforms();

			bool ChunksDir = false;
			if(SaveDir.DirExists($"user://Saves/{SaveNameArg}/Chunks")) {
				SaveDir.Open($"user://Saves/{SaveNameArg}/Chunks");
				ChunksDir = true;
			}
			else
				SaveDir.Open($"user://Saves/{SaveNameArg}");
			SaveDir.ListDirBegin(true, true);

			int PlaceCount = 0;
			while(true) {
				string FileName = SaveDir.GetNext();
				if(FileName.Empty()) {
					//Iterated through all files
					break;
				}

				string Path;
				if(ChunksDir)
					Path = $"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Chunks/{FileName}";
				else
					Path = $"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/{FileName}";
				LoadChunk(System.IO.File.ReadAllText(Path)); //TODO: Try catch
			}

			SaveName = SaveNameArg;
			Console.Log($"Loaded {PlaceCount.ToString()} structures from save '{SaveNameArg}'");

			//TODO: Save/load player data in savefile
			Net.Players.Add(Self.GetTree().GetNetworkUniqueId(), new Net.PlayerData());

			if(Net.DedicatedServer) {
				Net.Nicknames[Net.ServerId] = "DedicatedServer";
			}
			else {
				Net.Nicknames[Net.ServerId] = Game.Nickname;
				Game.SpawnPlayer(Self.GetTree().GetNetworkUniqueId(), true);
			}

			return true;
		}
		else {
			SaveName = null;
			Console.ThrowLog($"Failed to load save '{SaveNameArg}' as it does not exist");
			return false;
		}
	}


	public static Vector3 GetChunkPos(Vector3 Position) {
		return new Vector3(Mathf.RoundToInt(Position.x / ChunkSize) * ChunkSize, 0, Mathf.RoundToInt(Position.z / ChunkSize) * ChunkSize);
	}


	public static Tuple<int, int> GetChunkTuple(Vector3 Position) {
		return new Tuple<int, int>(Mathf.RoundToInt(Position.x / ChunkSize) * ChunkSize, Mathf.RoundToInt(Position.z / ChunkSize) * ChunkSize);
	}


	static bool ChunkExists(Vector3 Position) {
		return ChunkExists(GetChunkTuple(Position));
	}


	static bool ChunkExists(Tuple<int, int> Position) {
		return Chunks.ContainsKey(Position);
	}


	public static bool ChunkWithinDistanceFrom(Tuple<int, int> ChunkTuple, int Distance, Vector3 From) {
		Vector3 ChunkPos = new Vector3(ChunkTuple.Item1, 0, ChunkTuple.Item2);
		From = GetChunkPos(From);
		return From.DistanceTo(ChunkPos) <= Distance * ChunkSize;
	}


	public static void AddEntityToChunk(IEntity Entity) {
		var ChunkTuple = GetChunkTuple(Entity.Translation);

		if(Chunks.TryGetValue(ChunkTuple, out var Chunk))
			Chunk.Entities.Add(Entity);
		else {
			Chunk = new ChunkClass();
			Chunk.Entities.Add(Entity);
			Chunks.Add(ChunkTuple, Chunk);
		}

		Entity.CurrentChunk = ChunkTuple;
	}


	public static void RemoveEntityFromChunk(IEntity Entity) {
		if(Chunks.TryGetValue(Entity.CurrentChunk, out var Chunk)) {
			Chunk.Entities.Remove(Entity);

			if(Chunk.IsEmpty())
				Chunks.Remove(Entity.CurrentChunk);
		}
	}


	public static List<IEntity> GetEntitiesWithinArea(Vector3 Center, float Radius) {
		//TODO: Make this more generic, more performant
		Assert.ActualAssert(Radius < ChunkSize);

		var Output = new List<IEntity>();

		var ChunkList = new List<Tuple<int, int>>();
		ChunkList.Add(GetChunkTuple(Center));

		ChunkList.Add(GetChunkTuple(Center + new Vector3(ChunkSize, 0, 0))); //Forward
		ChunkList.Add(GetChunkTuple(Center + new Vector3(-ChunkSize, 0, 0))); //Backward

		ChunkList.Add(GetChunkTuple(Center + new Vector3(0, 0, ChunkSize))); //Right
		ChunkList.Add(GetChunkTuple(Center + new Vector3(0, 0, -ChunkSize))); //Left

		ChunkList.Add(GetChunkTuple(Center + new Vector3(ChunkSize, 0, ChunkSize))); //Forward right
		ChunkList.Add(GetChunkTuple(Center + new Vector3(ChunkSize, 0, -ChunkSize))); //Forward left

		ChunkList.Add(GetChunkTuple(Center + new Vector3(-ChunkSize, 0, ChunkSize))); //Backward right
		ChunkList.Add(GetChunkTuple(Center + new Vector3(-ChunkSize, 0, -ChunkSize))); //Backward left

		foreach(var ChunkTuple in ChunkList) {
			if(Chunks.TryGetValue(ChunkTuple, out ChunkClass Chunk))
				Output = Output.Concat(Chunk.Entities).ToList(); //TODO: Eww
		}

		return Output;
	}


	static void AddTileToChunk(Tile Branch) {
		if(ChunkExists(Branch.Translation)) {
			List<Tile> Chunk = Chunks[GetChunkTuple(Branch.Translation)].Tiles;
			Chunk.Add(Branch);
			Chunks[GetChunkTuple(Branch.Translation)].Tiles = Chunk;
		}
		else {
			ChunkClass Chunk = new ChunkClass();
			Chunk.Tiles = new List<Tile> { Branch };
			Chunks.Add(GetChunkTuple(Branch.Translation), Chunk);
		}
	}


	public static void AddMobToChunk(MobClass Mob) {
		var ChunkTuple = GetChunkTuple(Mob.Translation);
		Mob.DepreciatedCurrentChunkTuple = ChunkTuple;

		if(ChunkExists(Mob.Translation))
			Chunks[ChunkTuple].Mobs.Add(Mob);
		else {
			ChunkClass Chunk = new ChunkClass();
			Chunk.Mobs.Add(Mob);
			Chunks.Add(ChunkTuple, Chunk);
		}
	}


	public static void AddItemToChunk(DroppedItem Item) {
		if(ChunkExists(Item.Translation)) {
			List<DroppedItem> Items = Chunks[GetChunkTuple(Item.Translation)].Items;
			Items.Add(Item);
			Chunks[GetChunkTuple(Item.Translation)].Items = Items;
		}
		else {
			ChunkClass Chunk = new ChunkClass();
			Chunk.Items.Add(Item);
			Chunks.Add(GetChunkTuple(Item.Translation), Chunk);
		}
	}


	public static void PlaceOn(Items.ID BranchType, Tile Base, float PlayerOrientation, int BuildRotation, Vector3 HitPoint, int OwnerId) {
		Vector3? Position = Items.TryCalculateBuildPosition(BranchType, Base, PlayerOrientation, BuildRotation, HitPoint);
		if(Position != null) //If null then unsupported branch/base combination
		{
			Vector3 Rotation = Items.CalculateBuildRotation(BranchType, Base, PlayerOrientation, BuildRotation, HitPoint);
			string GuidName = System.Guid.NewGuid().ToString();
			Self.PlaceWithName(BranchType, (Vector3)Position, Rotation, OwnerId, GuidName);
			Net.SteelRpc(Self, nameof(PlaceWithName), BranchType, (Vector3)Position, Rotation, OwnerId, GuidName);
		}
	}

	[Remote]
	public void PlaceWithName(Items.ID BranchType, Vector3 Position, Vector3 Rotation, int OwnerId, string Name) {
		//Nested if to prevent very long line
		if(Net.Work.NetworkPeer != null && !Net.Work.IsNetworkServer() && Game.PossessedPlayer.HasValue) {
			Player Plr = Game.PossessedPlayer.ValueOrFailure();
			var PositionChunk = GetChunkTuple(Position);
			if(!ChunkWithinDistanceFrom(PositionChunk, Game.ChunkRenderDistance, Plr.Translation)) {
				//If network is inited, not the server, and platform it to far away then...
				return; //...don't place
			}
		}

		var Branch = (Tile)Scenes[BranchType].Instance();
		Branch.ItemId = BranchType;
		Branch.OwnerId = OwnerId;
		Branch.Translation = Position;
		Branch.RotationDegrees = Rotation;
		Branch.Name = Name; //Name is a GUID and can be used to reference a structure over network
		EntitiesRoot.AddChild(Branch);

		AddTileToChunk(Branch);
		Grid.AddItem(Branch);
		Grid.QueueUpdateNearby(Branch.Translation);

		if(Net.Work.NetworkPeer != null && Net.Work.IsNetworkServer()) {
			TryAddTileToPathfinder(Branch);

			if(Game.PossessedPlayer.HasValue) {
				Player Plr = Game.PossessedPlayer.ValueOrFailure();
				var PositionChunk = GetChunkTuple(Position);
				if(!ChunkWithinDistanceFrom(PositionChunk, Game.ChunkRenderDistance, Plr.Translation)) {
					//If network is inited, am the server, and platform is to far away then...
					Branch.Hide(); //...make it not visible but allow it to remain in the world
				}
			}

			foreach(int Id in Net.Players.Keys) {
				if(Id == Net.ServerId) //Skip self (we are the server)
					continue;

				Net.Players[Id].Plr.MatchSome(
					(Plr) => {
						var PositionChunk = GetChunkTuple(Position);
						if(ChunkWithinDistanceFrom(PositionChunk, ChunkRenderDistances[Id], Plr.Translation)) {
							if(!RemoteLoadedChunks[Id].Contains(GetChunkTuple(Position)))
								RemoteLoadedChunks[Id].Add(GetChunkTuple(Position));
						}
					}
				);
			}
		}
	}


	public static void TryAddTileToPathfinder(Tile Branch) {
		Tile TryGetPlatform(Vector3 Position, Vector3 RaycastOffset = new Vector3()) {
			var Area = GridClass.CalculateArea(Position);
			foreach(IInGrid Entry in Grid.GetItems(Area)) {
				if(Entry is Tile OtherTile && OtherTile.ItemId == Items.ID.PLATFORM) {
					PhysicsDirectSpaceState State = Branch.GetWorld().DirectSpaceState;

					var RayBranchPos = Branch.Point.Pos + RaycastOffset;
					var RayOtherPos = OtherTile.Point.Pos + RaycastOffset;
					var Excluding = new Godot.Collections.Array { };

					var Results = State.IntersectRay(RayBranchPos, RayOtherPos, Excluding, 4);
					if(Results.Count > 0) //Hit something in between
						return null;
					else
						return OtherTile;
				}
			}

			return null;
		}

		Tile TryGetSlope(Vector3 Position, Vector3 RaycastOffset = new Vector3()) {
			var Area = GridClass.CalculateArea(Position);
			foreach(IInGrid Entry in Grid.GetItems(Area)) {
				if(Entry is Tile OtherTile && OtherTile.ItemId == Items.ID.SLOPE) {
					PhysicsDirectSpaceState State = Branch.GetWorld().DirectSpaceState;

					var RayBranchPos = Branch.Point.Pos + RaycastOffset;
					var RayOtherPos = OtherTile.Point.Pos + RaycastOffset;
					var Excluding = new Godot.Collections.Array { };

					var Results = State.IntersectRay(RayBranchPos, RayOtherPos, Excluding, 4);
					if(Results.Count > 0) //Hit something in between
						return null;
					else
						return OtherTile;
				}
			}

			return null;
		}

		bool IsSlopePointingAt(Tile Slope, Vector3 At) {
			float Rot = SnapToGrid(LoopRotation(Slope.RotationDegrees.y), 360, 4);
			Rot = LoopRotation(Rot); //Make 360 become 0

			Vector3 Addend = new Vector3(0, 0, PlatformSize / 3).Rotated(new Vector3(0, 1, 0), Deg2Rad(Rot));
			Vector3 SlopePos = Slope.Translation + Addend;

			if(Rot == 0)
				return At.z >= SlopePos.z;
			if(Rot == 90)
				return At.x >= SlopePos.x;
			if(Rot == 180)
				return At.z <= SlopePos.z;
			if(Rot == 270)
				return At.x <= SlopePos.x;

			throw new Exception("This `if` chain should have 100% coverage");
		}

		bool IsSlopePointingAway(Tile Slope, Vector3 At) {
			float Rot = SnapToGrid(LoopRotation(Slope.RotationDegrees.y), 360, 4);
			Rot = LoopRotation(Rot); //Make 360 become 0

			Vector3 Addend = new Vector3(0, 0, -PlatformSize / 3).Rotated(new Vector3(0, 1, 0), Deg2Rad(Rot));
			Vector3 SlopePos = Slope.Translation + Addend;

			if(Rot == 0)
				return At.z <= SlopePos.z;
			if(Rot == 90)
				return At.x <= SlopePos.x;
			if(Rot == 180)
				return At.z >= SlopePos.z;
			if(Rot == 270)
				return At.x >= SlopePos.x;

			throw new Exception("This `if` chain should have 100% coverage");
		}

		switch(Branch.ItemId) {
			case (Items.ID.PLATFORM): {
				Branch.Point = Pathfinder.AddPoint(Branch.Translation + new Vector3(0, 2, 0));

				{ //Connect to platforms in the eight spaces around us
					var AheadPos = Branch.Translation + new Vector3(0, 1, PlatformSize);
					var BehindPos = Branch.Translation + new Vector3(0, 1, -PlatformSize);
					var RightPos = Branch.Translation + new Vector3(-PlatformSize, 1, 0);
					var LeftPos = Branch.Translation + new Vector3(PlatformSize, 1, 0);

					Tile Ahead = TryGetPlatform(AheadPos);
					Tile Behind = TryGetPlatform(BehindPos);
					Tile Right = TryGetPlatform(RightPos);
					Tile Left = TryGetPlatform(LeftPos);

					if(Ahead != null)
						Pathfinder.ConnectPoints(Branch.Point, Ahead.Point);
					if(Behind != null)
						Pathfinder.ConnectPoints(Branch.Point, Behind.Point);
					if(Right != null)
						Pathfinder.ConnectPoints(Branch.Point, Right.Point);
					if(Left != null)
						Pathfinder.ConnectPoints(Branch.Point, Left.Point);

					var AheadRightPos = Branch.Translation + new Vector3(-PlatformSize, 1, PlatformSize);
					var AheadLeftPos = Branch.Translation + new Vector3(PlatformSize, 1, PlatformSize);
					var BehindRightPos = Branch.Translation + new Vector3(-PlatformSize, 1, -PlatformSize);
					var BehindLeftPos = Branch.Translation + new Vector3(PlatformSize, 1, -PlatformSize);

					Tile AheadRight = null;
					Tile AheadLeft = null;
					Tile BehindRight = null;
					Tile BehindLeft = null;

					if(Ahead != null && Right != null)
						AheadRight = TryGetPlatform(AheadRightPos);
					if(Ahead != null && Left != null)
						AheadLeft = TryGetPlatform(AheadLeftPos);
					if(Behind != null && Right != null)
						BehindRight = TryGetPlatform(BehindRightPos);
					if(Behind != null && Left != null)
						BehindLeft = TryGetPlatform(BehindLeftPos);

					if(AheadRight != null)
						Pathfinder.ConnectPoints(Branch.Point, AheadRight.Point);
					if(AheadLeft != null)
						Pathfinder.ConnectPoints(Branch.Point, AheadLeft.Point);
					if(BehindRight != null)
						Pathfinder.ConnectPoints(Branch.Point, BehindRight.Point);
					if(BehindLeft != null)
						Pathfinder.ConnectPoints(Branch.Point, BehindLeft.Point);
				}

				{ //Connect to slopes in the four cardinal directions (same Y)
					var AheadPos = Branch.Translation + new Vector3(0, 1, PlatformSize);
					var BehindPos = Branch.Translation + new Vector3(0, 1, -PlatformSize);
					var RightPos = Branch.Translation + new Vector3(-PlatformSize, 1, 0);
					var LeftPos = Branch.Translation + new Vector3(PlatformSize, 1, 0);

					Tile Ahead = TryGetSlope(AheadPos);
					Tile Behind = TryGetSlope(BehindPos);
					Tile Right = TryGetSlope(RightPos);
					Tile Left = TryGetSlope(LeftPos);

					Vector3 Pos = Branch.Translation;
					if(Ahead != null && IsSlopePointingAway(Ahead, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Ahead.Point);
					if(Behind != null && IsSlopePointingAway(Behind, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Behind.Point);
					if(Right != null && IsSlopePointingAway(Right, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Right.Point);
					if(Left != null && IsSlopePointingAway(Left, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Left.Point);
				}

				{ //Connect to slopes in the four cardinal directions (lower Y)
					var AheadPos = Branch.Translation + new Vector3(0, -PlatformSize + 1, PlatformSize);
					var BehindPos = Branch.Translation + new Vector3(0, -PlatformSize + 1, -PlatformSize);
					var RightPos = Branch.Translation + new Vector3(-PlatformSize, -PlatformSize + 1, 0);
					var LeftPos = Branch.Translation + new Vector3(PlatformSize, -PlatformSize + 1, 0);

					var RaycastOffset = new Vector3(0, PlatformSize / 4, 0);
					Tile Ahead = TryGetSlope(AheadPos, RaycastOffset);
					Tile Behind = TryGetSlope(BehindPos, RaycastOffset);
					Tile Right = TryGetSlope(RightPos, RaycastOffset);
					Tile Left = TryGetSlope(LeftPos, RaycastOffset);

					Vector3 Pos = Branch.Translation;
					if(Ahead != null && IsSlopePointingAt(Ahead, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Ahead.Point);
					if(Behind != null && IsSlopePointingAt(Behind, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Behind.Point);
					if(Right != null && IsSlopePointingAt(Right, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Right.Point);
					if(Left != null && IsSlopePointingAt(Left, Pos))
						Pathfinder.ConnectPoints(Branch.Point, Left.Point);
				}

				break;
			}

			case (Items.ID.SLOPE): {
				Branch.Point = Pathfinder.AddPoint(Branch.Translation + new Vector3(0, 2, 0));

				var Axis = new Vector3(0, 1, 0);
				var Rad = Branch.Rotation.y;

				{ //Connect to other slopes in four cardinal directions
					var AheadPos = Branch.Translation + new Vector3(0, PlatformSize, PlatformSize).Rotated(Axis, Rad);
					var BehindPos = Branch.Translation + new Vector3(0, -PlatformSize, -PlatformSize).Rotated(Axis, Rad);
					var RightPos = Branch.Translation + new Vector3(-PlatformSize, 0, 0).Rotated(Axis, Rad);
					var LeftPos = Branch.Translation + new Vector3(PlatformSize, 0, 0).Rotated(Axis, Rad);

					var RaycastOffset = new Vector3(0, PlatformSize / 4, 0);
					Tile Ahead = TryGetSlope(AheadPos, RaycastOffset);
					Tile Behind = TryGetSlope(BehindPos, RaycastOffset);
					Tile Right = TryGetSlope(RightPos);
					Tile Left = TryGetSlope(LeftPos);

					if(Ahead != null)
						Pathfinder.ConnectPoints(Branch.Point, Ahead.Point);
					if(Behind != null)
						Pathfinder.ConnectPoints(Branch.Point, Behind.Point);
					if(Right != null)
						Pathfinder.ConnectPoints(Branch.Point, Right.Point);
					if(Left != null)
						Pathfinder.ConnectPoints(Branch.Point, Left.Point);
				}

				{ //Connect to platforms forward and backwards
					var AheadPos = Branch.Translation + new Vector3(0, PlatformSize / 2, PlatformSize).Rotated(Axis, Rad);
					var BehindPos = Branch.Translation + new Vector3(0, 0, -PlatformSize).Rotated(Axis, Rad);

					var RaycastOffset = new Vector3(0, PlatformSize / 4, 0);
					Tile Ahead = TryGetPlatform(AheadPos, RaycastOffset);
					Tile Behind = TryGetPlatform(BehindPos);

					if(Ahead != null)
						Pathfinder.ConnectPoints(Branch.Point, Ahead.Point);
					if(Behind != null)
						Pathfinder.ConnectPoints(Branch.Point, Behind.Point);
				}

				break;
			}

			default:
				return;
		}
	}


	[SteelInputWithoutArg(typeof(World), nameof(DrawPathfinderConnections))]
	public static void DrawPathfinderConnections() {
		Game.PossessedPlayer.MatchSome(
			(Plr) => {
				foreach(var Point in Pathfinder.Points) {
					foreach(var Friend in Point.Friends) {
						if(Friend.Pos.DistanceTo(Plr.Translation) > PlatformSize * 3)
							continue;

						Hitscan.Self.DrawTrail(Point.Pos, Friend.Pos);
					}
				}
			}
		);
	}


	//Name is the string GUID name of the structure to be removed
	[Remote]
	public void RemoveTile(string TileName) {
		if(EntitiesRoot.HasNode(TileName)) {
			var Branch = EntitiesRoot.GetNode<Tile>(TileName);
			Tuple<int, int> ChunkTuple = GetChunkTuple(Branch.Translation);
			Chunks[ChunkTuple].Tiles.Remove(Branch);
			if(Chunks[ChunkTuple].Tiles.Count <= 0 && Chunks[ChunkTuple].Items.Count <= 0) {
				//If the chunk is empty then remove it
				Chunks.Remove(ChunkTuple);
			}

			if(Branch.Point != null)
				World.Pathfinder.RemovePoint(Branch.Point);

			Grid.QueueUpdateNearby(Branch.Translation);
			Grid.QueueRemoveItem(Branch);
			Branch.OnRemove();
			Branch.QueueFree();
		}
	}


	public static void RemoveDroppedItem(string Guid) {
		if(EntitiesRoot.HasNode(Guid)) {
			var Item = EntitiesRoot.GetNode<DroppedItem>(Guid);
			Tuple<int, int> ChunkTuple = GetChunkTuple(Item.Translation);
			Chunks[ChunkTuple].Items.Remove(Item);
			if(Chunks[ChunkTuple].Tiles.Count <= 0 && Chunks[ChunkTuple].Items.Count <= 0) {
				//If the chunk is empty then remove it
				Chunks.Remove(ChunkTuple);
			}

			Grid.QueueRemoveItem(Item);
			ItemList.Remove(Item);
			Item.QueueFree();
		}
	}


	[Remote]
	public void InitialNetWorldSend(int Id, Vector3 PlayerPosition, int RenderDistance) {
		if(!Net.Work.IsNetworkServer())
			throw new Exception($"Attempted to run {nameof(InitialNetWorldSend)} on client");

		RequestChunks(Id, PlayerPosition, RenderDistance);

		Game.SpawnPlayer(Id, false);
		Assert.ActualAssert(Net.Players[Id].Plr.HasValue);
		Net.Players[Id].Plr.ValueOrFailure().SetFreeze(false);
		Net.Players[Id].Plr.ValueOrFailure().GiveDefaultItems();
	}


	public static void UnloadAndRequestChunks(Vector3 Position, int ChunkRenderDistance) {
		if(!IsOpen)
			return;

		var DeadChunks = new List<Tuple<int, int>>();

		foreach(KeyValuePair<Tuple<int, int>, ChunkClass> Chunk in Chunks.ToArray()) {
			if(World.ChunkWithinDistanceFrom(Chunk.Key, ChunkRenderDistance, Position)) {
				if(Self.GetTree().IsNetworkServer()) {
					foreach(IEntity Entity in Chunk.Value.Entities)
						Entity.Visible = true;
				}
				//Client logic is handled by RPC calling `RequestChunks` on the server
			}
			else {
				if(Net.Work.IsNetworkServer()) {
					foreach(IEntity Entity in Chunk.Value.Entities)
						Entity.Visible = false;
				}
				else {
					var EntitiesBeingRemoved = new List<IEntity>(Chunk.Value.Entities);
					foreach(IEntity Entity in EntitiesBeingRemoved)
						Entity.PhaseOut();

					DeadChunks.Add(Chunk.Key);
				}
			}
		}

		foreach(var ChunkTuple in DeadChunks)
			Chunks.Remove(ChunkTuple);

		if(!Self.GetTree().IsNetworkServer())
			Self.RequestChunks(Self.GetTree().GetNetworkUniqueId(), Position.Flattened(), ChunkRenderDistance);
	}


	[Remote]
	public void RequestChunks(int Id, Vector3 Position, int RenderDistance) //Can be called non-rpc by passing int id
	{
		if(!GetTree().IsNetworkServer()) {
			RpcId(Net.ServerId, nameof(RequestChunks), Id, Position, RenderDistance);
			return; //If not already on the server run on server and return early on client
		}

		if(!Net.Players.ContainsKey(Id)) { return; }

		ChunkRenderDistances[Id] = RenderDistance;

		foreach(KeyValuePair<System.Tuple<int, int>, ChunkClass> Chunk in Chunks) {
			Vector3 ChunkPos = new Vector3(Chunk.Key.Item1, 0, Chunk.Key.Item2);
			Tuple<int, int> ChunkTuple = GetChunkTuple(ChunkPos);
			if(ChunkWithinDistanceFrom(ChunkTuple, RenderDistance, Position)) {
				//This chunk is close enough to the player that we should send it along
				if(RemoteLoadedChunks[Id].Contains(ChunkTuple) || RemoteLoadingChunks[Id].Contains(ChunkTuple)) { } //If already loaded or loading then don't send it
				else {
					RemoteLoadingChunks[Id].Add(ChunkTuple);
					SendChunk(Id, ChunkTuple);
				}
			}
			else {
				//This chunk is to far away
				if(RemoteLoadedChunks[Id].Contains(ChunkTuple)) {
					//If it is in the list of loaded chunks for this client then remove
					RemoteLoadedChunks[Id].Remove(ChunkTuple);
				}
			}
		}
	}


	static void SendChunk(int Id, Tuple<int, int> ChunkLocation) {
		Self.RpcId(Id, nameof(PrepareChunkSpace), new Vector2(ChunkLocation.Item1, ChunkLocation.Item2));

		foreach(IEntity Entity in Chunks[ChunkLocation].Entities) {
			Entities.SendCreateTo(Id, Entity);
			if(Entity is IHasInventory HasInventory)
				Entities.SendInventoryTo(HasInventory, Id);
		}

		//After sending all the chunk data lets tell the client that its all
		Self.RpcId(Id, nameof(NotifyEndOfChunk), ChunkLocation.Item1, ChunkLocation.Item2);
	}


	[Remote]
	public void NotifyEndOfChunk(int X, int Z) //Runs on client at the end of chunk load RPCs
	{
		if(Net.Work.IsNetworkServer())
			throw new Exception($"{nameof(MarkChunkLoaded)} was executed on the server");

		RpcId(Net.ServerId, nameof(MarkChunkLoaded), X, Z);
	}


	[Remote]
	public void MarkChunkLoaded(int X, int Z) //Run on server by client after receiving all chunk load RPCs
	{
		if(!Net.Work.IsNetworkServer())
			throw new Exception($"{nameof(MarkChunkLoaded)} was executed on a client");

		int SenderId = Net.Work.GetRpcSenderId();
		Assert.ActualAssert(SenderId != 0);

		var ChunkTuple = new Tuple<int, int>(X, Z);
		RemoteLoadingChunks[SenderId].Remove(ChunkTuple);
		RemoteLoadedChunks[SenderId].Add(ChunkTuple);
	}


	public static int SaveChunk(Tuple<int, int> ChunkTuple, string SaveNameArg) {
		string SerializedChunk = Newtonsoft.Json.JsonConvert.SerializeObject(new SavedChunk(ChunkTuple));
		System.IO.File.WriteAllText($"{OS.GetUserDataDir()}/Saves/{SaveNameArg}/Chunks/{ChunkTuple.ToString()}.json", SerializedChunk);

		int SaveCount = 0;
		foreach(Tile Branch in Chunks[ChunkTuple].Tiles) //I hate to do this because it is rather inefficient
		{
			if(Branch.OwnerId != 0) {
				SaveCount += 1;
			}
		}
		return SaveCount;
	}


	//Doesn't actually have to be a single chunk
	//NOTE: Written for the happy path, throws exceptions on failure
	public static void LoadChunk(string ToLoad) {
		SavedChunk LoadedChunk = Newtonsoft.Json.JsonConvert.DeserializeObject<SavedChunk>(ToLoad);

		foreach(SavedTile Saved in LoadedChunk.Tiles) {
			string GuidName = System.Guid.NewGuid().ToString();
			Self.PlaceWithName(Saved.Id, Saved.Pos, Saved.Rot, Saved.Owner, GuidName);
			if(Saved.InventoryIndex >= 0) {
				Node Branch = EntitiesRoot.GetNode(GuidName);
				SavedInventory Inventory = LoadedChunk.Inventories[Saved.InventoryIndex];
				if(Branch is IHasInventory HasInventory && HasInventory.Inventory.Contents.Length == Inventory.Contents.Length)
					HasInventory.Inventory.Contents = Inventory.Contents;
			}
		}
	}


	[Remote]
	public void PrepareChunkSpace(Vector2 Pos) //Run on the client to clear a chunk's area before being populated from the server
	{
		ChunkClass ChunkToFree;
		if(Chunks.TryGetValue(new Tuple<int, int>((int)Pos.x, (int)Pos.y), out ChunkToFree)) //Chunk might not exist
		{
			foreach(Tile Branch in ChunkToFree.Tiles) {
				Branch.Free();
			}

			List<DroppedItem> ItemsToRemove = new List<DroppedItem>();
			foreach(DroppedItem Item in ChunkToFree.Items) {
				ItemsToRemove.Add(Item);
			}
			foreach(DroppedItem Item in ItemsToRemove) {
				Item.Remove();
			}

			Chunks.Remove(new Tuple<int, int>((int)Pos.x, (int)Pos.y));
		}
	}


	//Should be able to be called without RPC yet only run on server
	//Has to be non-static to be RPC-ed
	[Remote]
	public void DropItem(Items.ID Type, Vector3 Position, Vector3 BaseMomentum) {
		if(Self.GetTree().NetworkPeer != null) {
			float Rotation = Game.Rand.Next(0, 360); //Int cast to float, limited resolution is fine

			if(Self.GetTree().IsNetworkServer()) {
				string Name = System.Guid.NewGuid().ToString();
				DropOrUpdateItem(Type, Position, Rotation, BaseMomentum, Name);
				Net.SteelRpc(Self, nameof(DropOrUpdateItem), Type, Position, Rotation, BaseMomentum, Name);
			}
			else {
				Self.RpcId(Net.ServerId, nameof(DropItem), Type, Position, BaseMomentum);
			}
		}
	}


	//Has to be non-static to be RPC-ed
	[Remote]
	public void DropOrUpdateItem(Items.ID Type, Vector3 Position, float Rotation, Vector3 BaseMomentum, string Name) //Performs the actual drop
	{
		if(EntitiesRoot.HasNode(Name)) {
			DroppedItem Instance = EntitiesRoot.GetNode<DroppedItem>(Name);
			Instance.Translation = Position;
			Instance.RotationDegrees = new Vector3(0, Rotation, 0);
			Instance.Momentum = BaseMomentum;
			Instance.PhysicsEnabled = true;
		}
		else {
			Game.PossessedPlayer.MatchSome(
				(Plr) => {
					var PositionChunk = GetChunkTuple(Position);
					if(ChunkWithinDistanceFrom(PositionChunk, Game.ChunkRenderDistance, Plr.Translation)) {
						var ToDrop = (DroppedItem)DroppedItemScene.Instance();
						ToDrop.Translation = Position;
						ToDrop.RotationDegrees = new Vector3(0, Rotation, 0);
						ToDrop.Momentum = BaseMomentum;
						ToDrop.Type = Type;
						ToDrop.Name = Name;
						ToDrop.GetNode<MeshInstance>("MeshInstance").Mesh = Items.Meshes[Type];

						AddItemToChunk(ToDrop);
						ItemList.Add(ToDrop);
						EntitiesRoot.AddChild(ToDrop);
					}
				}
			);
		}
	}


	public override void _Process(float Delta) {
		if(IsOpen) {
			if(Net.Work.IsNetworkServer()) {
				TimeOfDay += Delta;
				if(TimeOfDay >= 60f * DayNightMinutes)
					TimeOfDay -= 60 * DayNightMinutes;
				TimeOfDay = Clamp(TimeOfDay, 0, 60f * DayNightMinutes);
				Net.SteelRpcUnreliable(this, nameof(NetUpdateTime), TimeOfDay);
			}

			Grid.DoWork();

			WorldSky.SunLatitude = TimeOfDay * (360f / (DayNightMinutes * 60f));

			float LightTime = TimeOfDay;
			if(LightTime > 15f * DayNightMinutes)
				LightTime = Clamp((30f * DayNightMinutes) - LightTime, 0, 15f * DayNightMinutes);
			float Power = Clamp(((LightTime) / (DayNightMinutes * 30f)) * 5f, 0, 1);

			WorldEnv.AmbientLightEnergy = Clamp(Power, 0.05f, 1);

			Color DaySkyTop = new Color(179f / 255f, 213f / 255f, 255f / 255f, 1);
			Color MorningSkyTop = new Color(34f / 255f, 50f / 255f, 78f / 255f, 1);
			Color NightSkyTop = new Color(27.2f / 255f, 40f / 255f, 62.4f / 255f, 1);

			Color DayHorizon = new Color(70f / 255f, 146f / 255f, 255f / 255f, 1);
			Color MorningHorizon = new Color(222f / 255f, 129f / 255f, 73f / 255f, 1);
			Color NightHorizon = NightSkyTop;


			Color DayGround = new Color(134f / 255f, 195f / 255f, 255f / 255f, 1);
			Color MorningGround = new Color(20f / 255f, 29f / 255f, 44f / 255f, 1);

			if(TimeOfDay <= DayNightMinutes * 60 / 2) {
				WorldSky.SkyTopColor = LerpColor(MorningSkyTop, DaySkyTop, Power);
				WorldSky.SkyHorizonColor = LerpColor(MorningHorizon, DayHorizon, Power);
			}
			else {
				float Diff;
				if(TimeOfDay < DayNightMinutes * 45f)
					Diff = (DayNightMinutes * 45f - TimeOfDay) / (DayNightMinutes * 15f);
				else
					Diff = (TimeOfDay - DayNightMinutes * 45f) / (DayNightMinutes * 15f);
				Diff = Clamp(Diff, 0, 1);
				Diff = Pow(Diff, 10);

				WorldSky.SkyTopColor = LerpColor(NightSkyTop, MorningSkyTop, Diff);
				WorldSky.SkyHorizonColor = LerpColor(NightHorizon, MorningHorizon, Diff);
			}
			WorldSky.GroundHorizonColor = WorldSky.SkyHorizonColor;

			WorldSky.GroundBottomColor = LerpColor(MorningGround, DayGround, Power);
		}
	}


	[Remote]
	private void NetUpdateTime(float NewTime) {
		TimeOfDay = NewTime;
	}
}
